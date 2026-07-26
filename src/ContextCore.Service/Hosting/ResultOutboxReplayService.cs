using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// P0-4：结果 outbox 重放后台服务。
/// 从 <see cref="IPersistentKernelResultOutbox.LeaseAsync"/> 租约 Pending 结果，
/// 调用 <see cref="IAgentKernelTransport.SendResultAsync"/> 投递，成功后 <see cref="IPersistentKernelResultOutbox.AckAsync"/>。
/// 崩溃未 Ack 的行由 <see cref="LeaseReaperService"/> 回滚为 Pending 后重新重放。
/// </summary>
/// <remarks>
/// <b>重放语义</b>：outbox 中的结果是 Kernel 在 <c>FallbackToDeterministic</c> 策略下
/// SendResultAsync 失败后写入的。replayer 负责在 transport 恢复后重新投递。
/// 若 SendResultAsync 持续失败，replayer 通过 <see cref="DurableTransportHostingOptions.OutboxRetryBackoff"/>
/// 退避，避免 tight-loop 打满 DB。
///
/// <b>非持久化 outbox 降级</b>：若 outbox 未实现 <see cref="IPersistentKernelResultOutbox"/>（如 InMemory），
/// replayer 回退到 <see cref="IKernelResultOutbox.DequeueAsync"/>（遗留 API，无租约保护）。
/// </remarks>
internal sealed class ResultOutboxReplayService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<DurableTransportHostingOptions> _options;
    private readonly ILogger<ResultOutboxReplayService> _logger;
    private readonly string _owner;

    public ResultOutboxReplayService(
        IServiceProvider services,
        IOptions<DurableTransportHostingOptions> options,
        ILogger<ResultOutboxReplayService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _owner = options.Value.Owner ?? $"replay-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 32);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Result outbox replay service is disabled.");
            return;
        }

        _logger.LogInformation(
            "Result outbox replay service started. PollInterval={Poll}ms, LeaseDuration={Lease}, Owner={Owner}.",
            options.OutboxPollInterval.TotalMilliseconds, options.OutboxLeaseDuration, _owner);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var outbox = scope.ServiceProvider.GetService<IKernelResultOutbox>();
                if (outbox is null)
                {
                    // 无 outbox 注入；休眠后重试（可能 DI 配置变化）
                    await Task.Delay(options.OutboxPollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var transport = scope.ServiceProvider.GetRequiredService<IAgentKernelTransport>();

                // 优先使用持久化 outbox 的租约模型；否则回退到遗留 DequeueAsync
                if (outbox is IPersistentKernelResultOutbox persistent)
                {
                    var leased = await persistent.LeaseAsync(options.OutboxLeaseDuration, _owner, stoppingToken)
                        .ConfigureAwait(false);
                    if (leased is null)
                    {
                        await Task.Delay(options.OutboxPollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await SendAndAckAsync(transport, persistent, leased, options.OutboxRetryBackoff, stoppingToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 遗留路径：InMemory outbox，无租约保护（crash 即丢）
                    var result = await outbox.DequeueAsync(stoppingToken).ConfigureAwait(false);
                    if (result is null)
                    {
                        await Task.Delay(options.OutboxPollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await transport.SendResultAsync(result, stoppingToken).ConfigureAwait(false);
                        _logger.LogDebug("Replayed result {InstructionId} via legacy dequeue.", result.InstructionId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Legacy outbox replay failed for {InstructionId}; result lost (InMemory outbox has no requeue).",
                            result.InstructionId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay 循环异常；{Backoff}ms 后重试。", options.OutboxPollInterval.TotalMilliseconds);
                try { await Task.Delay(options.OutboxPollInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Result outbox replay service stopped.");
    }

    private async Task SendAndAckAsync(
        IAgentKernelTransport transport,
        IPersistentKernelResultOutbox persistent,
        LeasedOutboxResult leased,
        TimeSpan retryBackoff,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.SendResultAsync(leased.Result, cancellationToken).ConfigureAwait(false);
            await persistent.AckAsync(leased.OutboxId, leased.LeaseToken, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Replayed and acked result {InstructionId} (outboxId={OutboxId}).",
                leased.Result.InstructionId, leased.OutboxId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendResultAsync failed for {InstructionId}; Nack 回滚为 Pending，{Backoff}ms 后重试。",
                leased.Result.InstructionId, retryBackoff.TotalMilliseconds);
            try
            {
                await persistent.NackAsync(leased.OutboxId, leased.LeaseToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx, "Nack 失败：outboxId={OutboxId}（租约可能已过期，将由 reaper 回滚）。",
                    leased.OutboxId);
            }
            try { await Task.Delay(retryBackoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
