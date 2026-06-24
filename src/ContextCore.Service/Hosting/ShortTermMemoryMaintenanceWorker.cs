using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>后台短期记忆维护任务，只执行 compaction/archive，不做 purge 或 promotion。</summary>
internal sealed class ShortTermMemoryMaintenanceWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ShortTermMaintenanceOptions> _options;
    private readonly ShortTermMaintenanceRuntimeState _state;
    private readonly ILogger<ShortTermMemoryMaintenanceWorker> _logger;

    public ShortTermMemoryMaintenanceWorker(
        IServiceProvider services,
        IOptions<ShortTermMaintenanceOptions> options,
        ShortTermMaintenanceRuntimeState state,
        ILogger<ShortTermMemoryMaintenanceWorker> logger)
    {
        _services = services;
        _options = options;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        _state.Configure(options);

        if (!options.Enabled)
        {
            _logger.LogInformation("Short-term maintenance worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds));
        _logger.LogInformation(
            "Short-term maintenance worker started. Interval={IntervalSeconds}s, RunOnStartup={RunOnStartup}.",
            interval.TotalSeconds,
            options.RunOnStartup);

        if (options.RunOnStartup)
        {
            await RunMaintenanceBatchAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunMaintenanceBatchAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunMaintenanceBatchAsync(CancellationToken cancellationToken)
    {
        _state.MarkRunning();
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetService<IShortTermMemoryStore>();
            var compaction = scope.ServiceProvider.GetService<ShortTermMemoryCompactionService>();
            if (store is null || compaction is null)
            {
                _logger.LogWarning("Short-term maintenance skipped because short-term services are unavailable.");
                return;
            }

            var scopes = await store.QueryScopesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var memoryScope in scopes)
            {
                var result = await compaction.CompactAsync(new ShortTermMemoryCompactionRequest
                {
                    WorkspaceId = memoryScope.WorkspaceId,
                    CollectionId = memoryScope.CollectionId
                }, trigger: "Scheduled", cancellationToken: cancellationToken).ConfigureAwait(false);

                if (result.Run is not null)
                {
                    _state.RecordRun(result.Run);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _state.RecordError(ex);
            _logger.LogError(ex, "Short-term maintenance worker failed.");
        }
        finally
        {
            _state.MarkIdle();
        }
    }
}
