using ContextCore.Abstractions;
using Microsoft.Extensions.Hosting;

namespace ContextCore.Service.Security;

// ===========================================================================
// ApiKeyPurgeWorker — 后台清理过期 / 已吊销 API Key
//
// 按 Security:ApiKeyRotation:PurgeInterval 周期性调用 IApiKeyStore.PurgeExpiredAsync。
// TimeSpan.Zero 时本 worker 不启动（由 AddContextCoreApiKeyPurgeWorker 跳过注册）。
//
// 设计：
//   1. 基于 BackgroundService + PeriodicTimer（轻量，无外部依赖）。
//   2. 异常隔离：单次清理失败不终止 worker（记录日志后继续下一周期）。
//   3. 优雅关闭：DisposeAsync 时取消等待中的 timer。
// ===========================================================================

/// <summary>
/// 后台清理过期 API Key 的 HostedService。
/// </summary>
public sealed class ApiKeyPurgeWorker : BackgroundService
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly SecurityOptions _options;
    private readonly ILogger<ApiKeyPurgeWorker> _logger;
    private PeriodicTimer? _timer;

    public ApiKeyPurgeWorker(
        IApiKeyStore apiKeyStore,
        SecurityOptions options,
        ILogger<ApiKeyPurgeWorker> logger)
    {
        _apiKeyStore = apiKeyStore;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.ApiKeyRotation.PurgeInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogDebug("ApiKeyPurgeWorker 已禁用（PurgeInterval=Zero）。");
            return;
        }

        _timer = new PeriodicTimer(interval);
        _logger.LogInformation(
            "ApiKeyPurgeWorker 已启动，每 {Interval} 清理一次过期 API Key。",
            interval);

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var purged = await _apiKeyStore.PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (purged > 0)
                    {
                        _logger.LogInformation("ApiKeyPurgeWorker 清理了 {Count} 个过期 API Key。", purged);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单次清理失败不应终止 worker
                    _logger.LogError(ex, "ApiKeyPurgeWorker 清理失败，等待下一周期重试。");
                }
            }
        }
        finally
        {
            _timer?.Dispose();
        }
    }
}
