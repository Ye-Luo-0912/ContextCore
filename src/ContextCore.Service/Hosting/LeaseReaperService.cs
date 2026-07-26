using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// P0-4：过期租约清理后台服务（lease reaper）。
/// 周期性调用 <see cref="IDurableTransport.RequeueExpiredAsync"/>（inbox + outbox 表）和
/// <see cref="IPersistentKernelResultOutbox.RequeueExpiredAsync"/>（result outbox 表），
/// 回滚过期 Leased 行为 Pending，使其可被 pump / replayer 重新租约。
/// </summary>
/// <remarks>
/// <b>触发场景</b>：worker 进程崩溃（持有租约但无法 Ack）、租约时长估计不足、网络分区。
/// reaper 确保崩溃 worker 持有的租约最终被释放，避免指令/结果永久滞留 Leased 状态。
///
/// <b>幂等性</b>：RequeueExpiredAsync 是幂等的——重复调用只会回滚仍未被 Ack 的过期行。
/// </remarks>
internal sealed class LeaseReaperService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<DurableTransportHostingOptions> _options;
    private readonly ILogger<LeaseReaperService> _logger;

    public LeaseReaperService(
        IServiceProvider services,
        IOptions<DurableTransportHostingOptions> options,
        ILogger<LeaseReaperService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Lease reaper service is disabled.");
            return;
        }

        _logger.LogInformation("Lease reaper started. Interval={Interval}s.", options.ReaperInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.ReaperInterval, stoppingToken).ConfigureAwait(false);

                using var scope = _services.CreateScope();
                var transport = scope.ServiceProvider.GetService<IAgentKernelTransport>();
                if (transport is IDurableTransport durable)
                {
                    var requeued = await durable.RequeueExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (requeued > 0)
                    {
                        _logger.LogInformation("Reaper requeued {Count} expired instruction/result leases from durable transport.", requeued);
                    }
                }

                var outbox = scope.ServiceProvider.GetService<IKernelResultOutbox>();
                if (outbox is IPersistentKernelResultOutbox persistent)
                {
                    var requeuedOutbox = await persistent.RequeueExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (requeuedOutbox > 0)
                    {
                        _logger.LogInformation("Reaper requeued {Count} expired result outbox leases.", requeuedOutbox);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reaper 循环异常；下一周期重试。");
            }
        }

        _logger.LogInformation("Lease reaper stopped.");
    }
}
