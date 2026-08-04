using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Worker 集群心跳服务：随应用启动周期标记集群存活。
/// 供生产准入校验的「最近心跳」检查消费——应用挂起或托管服务停止时心跳停止，
/// 准入校验将因心跳过期而拒绝放行，避免已失去执行能力的集群继续接流量。
/// </summary>
public sealed class ProductionWorkerFleetHeartbeatService : BackgroundService
{
    /// <summary>心跳间隔（默认 30 秒，远小于准入心跳窗口，容忍单次标记失败）。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly ProductionRuntimeWorkerRegistry _registry;
    private readonly ILogger<ProductionWorkerFleetHeartbeatService> _logger;

    /// <summary>构造函数。</summary>
    public ProductionWorkerFleetHeartbeatService(
        ProductionRuntimeWorkerRegistry registry,
        ILogger<ProductionWorkerFleetHeartbeatService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken).ConfigureAwait(false);
                _registry.MarkFleetHeartbeat();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker 集群心跳标记失败。");
            }
        }
    }
}
