using System.Threading.Channels;
using ContextCore.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// P0-4 / P1：Durable Transport 指令 pump 后台服务。
/// 从 <see cref="IDurableTransport.LeaseBatchAsync"/> 批量租约 Pending 指令，将 lease token 写入
/// <see cref="AgentKernelInstruction.Metadata"/>（键 <see cref="DurableTransportMetadataKeys.LeaseToken"/>），
/// 然后通过本地 bounded channel 串行调用 <see cref="IAgentKernel.SubmitAsync"/> 推入 Kernel 的 inbox channel。
/// Kernel 处理完成后（<see cref="DefaultAgentKernel.RunAsync"/> 循环内）自动调用
/// <see cref="IDurableTransport.AckAsync"/> 确认；崩溃未 Ack 的行由 <see cref="LeaseReaperService"/> 回滚。
/// </summary>
/// <remarks>
/// <b>P1：批量租约 + 指数退避</b>。
/// 单次 <see cref="IDurableTransport.LeaseBatchAsync"/> 拉取 <see cref="DurableTransportHostingOptions.BatchLeaseLimit"/>
/// 条指令，减少高并发下的网络往返；拉取到指令后写入本地 bounded channel 供 Kernel 消费（容量与 BatchLeaseLimit 一致）。
/// 连续空轮询时 polling interval 按 <see cref="DurableTransportHostingOptions.PollBackoffMultiplier"/> 指数增长
/// （上限 <see cref="DurableTransportHostingOptions.MaxPollInterval"/>）；拉取到指令时立即重置为
/// <see cref="DurableTransportHostingOptions.PollInterval"/>。
///
/// <b>关于 LISTEN/NOTIFY</b>：本项目设计明确回避 PostgreSQL LISTEN/NOTIFY 主动通知机制
/// （参见 <c>InvalidatingStoreDecoratorBase</c> 中的设计注释），仅用指数退避 + 有上限的轮询。
/// 未使用 LISTEN/NOTIFY 是项目设计决策，避免引入额外复杂度（额外连接、订阅生命周期管理、
/// 跨实例通知一致性等问题）。指数退避在空队列下显著降低 DB 压力，有数据时立即恢复快速轮询。
///
/// <b>幂等保证</b>：若 pump Lease + Submit 后 Kernel 崩溃未 Ack，reaper 回滚为 Pending，
/// pump 重新 Lease + Submit 同一指令。Kernel 内部通过 <c>_committedToolResults</c> 去重
/// 和 <c>_dispatchJournal</c> 幂等键保证 Execute 指令不会重复执行 tool。
/// Checkpoint / BuildContext 等非幂等指令由调用方保证幂等（如带幂等键）。
///
/// <b>启动顺序</b>：本服务启动后立即开始 pump。Kernel.RunAsync 应由独立的 HostedService 或调用方启动。
/// 若 Kernel 未运行，SubmitAsync 仍会成功（写入 channel），指令在 channel 中排队等待 Kernel 启动。
///
/// <b>降级路径</b>：若 <see cref="IDurableTransport.LeaseBatchAsync"/> 抛出 NotImplementedException 等异常
/// （兼容旧实现），pump 回退到单条 <see cref="IDurableTransport.LeaseAsync"/> 路径，保证向后兼容。
/// </remarks>
internal sealed class DurableTransportInstructionPumpService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<DurableTransportHostingOptions> _options;
    private readonly ILogger<DurableTransportInstructionPumpService> _logger;
    private readonly string _owner;

    public DurableTransportInstructionPumpService(
        IServiceProvider services,
        IOptions<DurableTransportHostingOptions> options,
        ILogger<DurableTransportInstructionPumpService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _owner = options.Value.Owner ?? $"pump-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 32);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Durable transport instruction pump is disabled.");
            return;
        }

        // P1：本地 bounded channel 用于在批量 lease 与 Kernel.SubmitAsync 之间解耦。
        // 容量取 BatchLeaseLimit（一次批量 lease 的最大条数），避免 lease 拉取快于 Kernel 消费时无限堆积。
        var batchLimit = Math.Max(1, options.BatchLeaseLimit);
        var localChannel = Channel.CreateBounded<AgentKernelInstruction>(new BoundedChannelOptions(batchLimit)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _logger.LogInformation(
            "Durable transport instruction pump started. PollInterval={Poll}ms, MaxPollInterval={MaxPoll}ms, " +
            "BackoffMultiplier={Mult}, BatchLeaseLimit={Batch}, LeaseDuration={Lease}, Owner={Owner}.",
            options.PollInterval.TotalMilliseconds,
            options.MaxPollInterval.TotalMilliseconds,
            options.PollBackoffMultiplier,
            batchLimit,
            options.InstructionLeaseDuration,
            _owner);

        // 启动消费者任务：从 localChannel 读取并调用 kernel.SubmitAsync。
        // 与 lease 循环并行执行，避免 SubmitAsync 阻塞下一次 lease。
        var consumerTask = Task.Run(() => ConsumeLocalChannelAsync(localChannel.Reader, stoppingToken), stoppingToken);

        try
        {
            await PumpLeaseLoopAsync(localChannel.Writer, batchLimit, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // lease 循环退出时关闭 channel 写入端，让消费者自然排空后退出
            localChannel.Writer.TryComplete();
            try
            {
                await consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 消费者因取消退出，正常
            }
        }

        _logger.LogInformation("Durable transport instruction pump stopped.");
    }

    /// <summary>
    /// P1：批量租约主循环。从 <see cref="IDurableTransport.LeaseBatchAsync"/> 拉取指令写入 localChannel；
    /// 连续空轮询时按 <see cref="DurableTransportHostingOptions.PollBackoffMultiplier"/> 指数退避，
    /// 上限 <see cref="DurableTransportHostingOptions.MaxPollInterval"/>；拉取到指令时立即重置为 PollInterval。
    /// </summary>
    private async Task PumpLeaseLoopAsync(ChannelWriter<AgentKernelInstruction> writer, int batchLimit, CancellationToken stoppingToken)
    {
        var options = _options.Value;
        var currentInterval = options.PollInterval;
        var maxInterval = options.MaxPollInterval > options.PollInterval ? options.MaxPollInterval : options.PollInterval;
        var multiplier = options.PollBackoffMultiplier > 1.0 ? options.PollBackoffMultiplier : 1.0;
        var batchLeaseFallbackLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var transport = scope.ServiceProvider.GetRequiredService<IAgentKernelTransport>();
                if (transport is not IDurableTransport durable)
                {
                    _logger.LogWarning("IAgentKernelTransport 不是 IDurableTransport；pump 退出。");
                    return;
                }

                IReadOnlyList<LeasedInstruction> leased;
                try
                {
                    leased = await durable.LeaseBatchAsync(batchLimit, options.InstructionLeaseDuration, _owner, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (NotImplementedException)
                {
                    // 降级：旧实现未提供 LeaseBatchAsync，回退到单条 LeaseAsync。
                    if (!batchLeaseFallbackLogged)
                    {
                        _logger.LogInformation("LeaseBatchAsync 不可用，回退到单条 LeaseAsync 路径。");
                        batchLeaseFallbackLogged = true;
                    }
                    var single = await durable.LeaseAsync(options.InstructionLeaseDuration, _owner, stoppingToken)
                        .ConfigureAwait(false);
                    leased = single is null ? Array.Empty<LeasedInstruction>() : new[] { single };
                }

                if (leased.Count == 0)
                {
                    // 空轮询：指数退避
                    await Task.Delay(currentInterval, stoppingToken).ConfigureAwait(false);
                    currentInterval = NextBackoffInterval(currentInterval, multiplier, maxInterval);
                    continue;
                }

                // 拉取到指令：重置 polling interval，写入 localChannel 供消费者处理
                currentInterval = options.PollInterval;

                foreach (var item in leased)
                {
                    // 将 lease token 写入 Metadata，Kernel 处理完成后据此 Ack
                    var instruction = item.Instruction with
                    {
                        Metadata = new Dictionary<string, string>(item.Instruction.Metadata, StringComparer.Ordinal)
                        {
                            [DurableTransportMetadataKeys.LeaseToken] = item.LeaseToken,
                            [DurableTransportMetadataKeys.LeaseOwner] = _owner,
                        },
                    };

                    await writer.WriteAsync(instruction, stoppingToken).ConfigureAwait(false);
                    _logger.LogDebug("Leased and queued instruction {InstructionId} (token={Token}).",
                        instruction.InstructionId, item.LeaseToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pump 循环异常；{Backoff}ms 后重试。", currentInterval.TotalMilliseconds);
                try { await Task.Delay(currentInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                // 异常后也按指数退避递增（避免 tight-loop 失败时打满 DB）
                currentInterval = NextBackoffInterval(currentInterval, multiplier, maxInterval);
            }
        }
    }

    /// <summary>
    /// P1：从 localChannel 读取指令并调用 <see cref="IAgentKernel.SubmitAsync"/> 推入 Kernel inbox。
    /// 与 lease 循环并行执行；channel 关闭后自然退出。
    /// </summary>
    private async Task ConsumeLocalChannelAsync(ChannelReader<AgentKernelInstruction> reader, CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<IAgentKernel>();

        await foreach (var instruction in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await kernel.SubmitAsync(instruction, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SubmitAsync 失败不影响 lease 循环；指令仍处于 Leased 状态，
                // 租约过期后由 LeaseReaperService 回滚为 Pending 重新租约。
                _logger.LogWarning(ex, "SubmitAsync 失败：instructionId={InstructionId}；租约将过期回滚。",
                    instruction.InstructionId);
            }
        }
    }

    /// <summary>
    /// P1：计算下一次退避间隔。currentInterval × multiplier，上限 maxInterval。
    /// multiplier ≤ 1.0 时直接返回 currentInterval（不退避）。
    /// </summary>
    private static TimeSpan NextBackoffInterval(TimeSpan currentInterval, double multiplier, TimeSpan maxInterval)
    {
        if (multiplier <= 1.0)
        {
            return currentInterval;
        }
        var nextMs = currentInterval.TotalMilliseconds * multiplier;
        var maxMs = maxInterval.TotalMilliseconds;
        if (nextMs >= maxMs)
        {
            return maxInterval;
        }
        return TimeSpan.FromMilliseconds(nextMs);
    }
}
