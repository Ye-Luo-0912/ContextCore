using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ContextCore.Abstractions;
using ContextCore.Core.Services.MemoryEvolution;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Learning Loop Durable Outbox 后台 worker。轮询 <see cref="ILearningEventOutboxStore"/> 中 pending 记录，
/// 通过 bounded Channel 分发到固定数量 worker 并行物化，Ack / Retry / DeadLetter。
/// </summary>
/// <remarks>
/// <para>
/// 仅 Postgres provider 时激活（<see cref="ILearningEventOutboxStore"/> 已注册）。
/// FileSystem / InMemory provider 时 worker 检测到 null 后立即退出——
/// 物化由 <see cref="LearningMaterializationDispatcher"/> 的 in-memory bounded Channel 处理。
/// </para>
/// <para>
/// 处理流程：
/// <list type="number">
/// <item>Poller：周期性 <see cref="ILearningEventOutboxStore.AcquirePendingAsync"/> 取出一批 pending 记录。</item>
/// <item>Dispatcher：将记录写入 bounded Channel（固定容量，背压控制）。</item>
/// <item>Worker（N 个固定任务）：从 Channel 读取记录 → 反序列化 payload → <see cref="UtilityLedgerMaterializer.MaterializeAsync"/> → Ack / DeadLetter。</item>
/// <item>失败时 <see cref="ILearningEventOutboxStore.MarkFailedAsync"/>——store 根据 retry_count 决定回退 Pending 或转 DeadLettered。</item>
/// </list>
/// </para>
/// <para>
/// 指标：每次 Ack 记录 materialization_lag + last_success_at；每次 MarkFailed 递增 failed_events；
/// 周期性 <see cref="ILearningEventOutboxStore.CountByStateAsync"/> 更新 pending_events / dead_letter_count。
/// </para>
/// </remarks>
public sealed class LearningMaterializationWorker : BackgroundService
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IServiceProvider _services;
    private readonly IOptions<LearningMaterializationOptions> _options;
    private readonly LearningMaterializationMetrics _metrics;
    private readonly ILogger<LearningMaterializationWorker> _logger;

    public LearningMaterializationWorker(
        IServiceProvider services,
        IOptions<LearningMaterializationOptions> options,
        LearningMaterializationMetrics metrics,
        ILogger<LearningMaterializationWorker> logger)
    {
        _services = services;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Learning materialization worker is disabled.");
            return;
        }

        // 检测 ILearningEventOutboxStore 是否注册——未注册时退出（FileSystem/InMemory provider 优雅降级）。
        using var probeScope = _services.CreateScope();
        var probeOutbox = probeScope.ServiceProvider.GetService<ILearningEventOutboxStore>();
        if (probeOutbox is null)
        {
            _logger.LogInformation(
                "Learning materialization worker detected no ILearningEventOutboxStore registered " +
                "(FileSystem/InMemory provider). Worker will exit — in-memory channel dispatch handles materialization.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds));
        var batchSize = Math.Max(1, options.BatchSize);
        var workerCount = Math.Max(1, options.WorkerCount);
        var leaseDuration = options.LeaseDuration;
        var heartbeatInterval = options.HeartbeatInterval;
        var owner = string.IsNullOrWhiteSpace(options.OwnerId)
            ? GenerateOwnerId()
            : options.OwnerId;

        // bounded Channel：poller → workers 之间的背压控制。
        var channelCapacity = Math.Max(workerCount * 2, batchSize);
        // P0-9：Channel 携带 (record, heartbeatCts) 对——Acquire 后立即启动 heartbeat，
        // 排队期间 lease 也会被续约，避免 record 在 Channel 中停留超过 leaseDuration 被抢占。
        var channel = Channel.CreateBounded<LearningMaterializationQueueItem>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

        _logger.LogInformation(
            "Learning materialization worker started. Interval={Interval}s, BatchSize={BatchSize}, Workers={Workers}, " +
            "Owner={Owner}, LeaseDuration={LeaseDuration}, ChannelCapacity={Capacity}.",
            interval.TotalSeconds, batchSize, workerCount, owner, leaseDuration, channelCapacity);

        // 启动固定 worker 任务。
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i;
            workers[i] = RunMaterializationWorkerAsync(channel.Reader, probeOutbox, owner, leaseDuration, heartbeatInterval, workerId, workerCts.Token);
        }

        // 周期性更新指标（outbox state counts + last_success_at）。
        var metricsTask = RunMetricsUpdaterAsync(probeOutbox, interval, stoppingToken);

        try
        {
            if (options.RunOnStartup)
            {
                await PollAndDispatchAsync(probeOutbox, channel.Writer, batchSize, owner, leaseDuration, heartbeatInterval, stoppingToken)
                    .ConfigureAwait(false);
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

                await PollAndDispatchAsync(probeOutbox, channel.Writer, batchSize, owner, leaseDuration, heartbeatInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // 信号 Channel 不再接受新写入，让 worker 排空。
            channel.Writer.TryComplete();

            try
            {
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                drainCts.CancelAfter(TimeSpan.FromSeconds(30));
                await Task.WhenAll(workers).WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                workerCts.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during learning materialization worker drain.");
                workerCts.Cancel();
            }

            try { await metricsTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { /* metrics task 超时忽略 */ }

            _logger.LogInformation("Learning materialization worker stopped.");
        }
    }

    /// <summary>
    /// 从 outbox 拉取一批 pending 记录并写入 bounded Channel。
    /// P0-9：每个 record 入 Channel 前立即启动 heartbeat 续约任务，与 record 一起通过 Channel 传递。
    /// 这样 record 在 Channel 排队期间 lease 也会被续约，避免被其他 worker 抢占。
    /// </summary>
    private async Task PollAndDispatchAsync(
        ILearningEventOutboxStore outboxStore,
        ChannelWriter<LearningMaterializationQueueItem> writer,
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LearningEventOutboxRecord> batch;
        try
        {
            batch = await outboxStore.AcquirePendingAsync(batchSize, owner, leaseDuration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire pending learning event outbox records.");
            return;
        }

        if (batch.Count == 0) return;

        _logger.LogDebug("Acquired {Count} learning event outbox records for materialization.", batch.Count);

        foreach (var record in batch)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // P0-9：Acquire 后立即启动 heartbeat 续约任务——不等到 worker 从 Channel 取出才启动。
            // heartbeat 与 worker 主循环解耦：worker 接管 queueItem 后复用此 heartbeatCts，
            // 处理完成或异常时 cancel。如果 record 未被消费（Channel 关闭/异常），heartbeatCts 也会
            // 在下面的 try-catch 中被 cancel，避免泄漏。
            var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = RunHeartbeatAsync(
                outboxStore, record.EventId, record.LeaseToken, leaseDuration, heartbeatInterval, heartbeatCts);

            try
            {
                await writer.WriteAsync(new LearningMaterializationQueueItem(record, heartbeatCts, heartbeatTask), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Channel 已关闭——取消 heartbeat 防止泄漏。
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); }
                catch { /* heartbeat 异常已在内部记录 */ }
                heartbeatCts.Dispose();
                break;
            }
        }
    }

    /// <summary>
    /// 固定 worker：从 Channel 读取 outbox 记录，反序列化 payload，调用 MaterializeAsync，Ack/DeadLetter。
    /// P0-9：heartbeat 任务已在 <see cref="PollAndDispatchAsync"/> 中启动并通过 Channel 传递——
    /// worker 复用 queueItem 中的 heartbeatCts，处理完成或异常时 cancel 停止 heartbeat。
    /// </summary>
    private async Task RunMaterializationWorkerAsync(
        ChannelReader<LearningMaterializationQueueItem> reader,
        ILearningEventOutboxStore outboxStore,
        string owner,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        int workerId,
        CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var materializer = scope.ServiceProvider.GetService<UtilityLedgerMaterializer>();

        if (materializer is null)
        {
            _logger.LogWarning("UtilityLedgerMaterializer unavailable — worker {WorkerId} cannot materialize.", workerId);
            return;
        }

        try
        {
            await foreach (var queueItem in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // 主循环取消——取消未处理 item 的 heartbeat 防止泄漏。
                    queueItem.HeartbeatCts.Cancel();
                    try { await queueItem.HeartbeatTask.ConfigureAwait(false); }
                    catch { /* heartbeat 异常已在内部记录 */ }
                    queueItem.HeartbeatCts.Dispose();
                    break;
                }

                var record = queueItem.Record;
                // P0-9：复用 PollAndDispatchAsync 已启动的 heartbeatCts/heartbeatTask——
                // 不再自行创建 leaseCts。处理完成或异常时通过 heartbeatCts.Cancel() 停止 heartbeat。
                var heartbeatCts = queueItem.HeartbeatCts;
                var heartbeatTask = queueItem.HeartbeatTask;

                _metrics.IncrementProcessing();

                try
                {
                    // 反序列化 payload。
                    var decision = JsonSerializer.Deserialize<ContextDecisionResult>(record.Payload, PayloadSerializerOptions);
                    if (decision is null)
                    {
                        await outboxStore.MarkFailedAsync(record.EventId, record.LeaseToken, "Failed to deserialize payload: null result.", cancellationToken)
                            .ConfigureAwait(false);
                        _metrics.IncrementFailed();
                        continue;
                    }

                    // 调用 MaterializeAsync。
                    await materializer.MaterializeAsync(
                        decision, record.WorkspaceId, record.CollectionId, cancellationToken)
                        .ConfigureAwait(false);

                    // Ack（需 lease_token 匹配——若 lease 已被抢占则 acked=false，当前 worker 应放弃该记录）。
                    var acked = await outboxStore.MarkAckedAsync(record.EventId, record.LeaseToken, cancellationToken).ConfigureAwait(false);
                    if (acked)
                    {
                        var lagMs = (DateTimeOffset.UtcNow - record.CreatedAt).TotalMilliseconds;
                        _metrics.RecordMaterializationLag(lagMs);
                        _metrics.RecordSuccess();
                    }
                    else
                    {
                        // lease 已被其他 worker 抢占——当前物化结果可能重复（新 worker 会重新物化），不记 success。
                        _logger.LogWarning(
                            "Outbox record {EventId} Ack 失败：lease 已被其他 worker 抢占（worker={WorkerId}）。",
                            record.EventId, workerId);
                    }
                }
                catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // 租约丢失——保留 state='Processing'，lease_expires_at 已过期或即将过期，
                    // 其他 worker 通过 AcquirePendingAsync 抢占。不调用 MarkAcked/MarkFailed。
                    _logger.LogWarning(
                        "Outbox record {EventId} aborted due to lease loss (worker={WorkerId}).",
                        record.EventId, workerId);
                    _metrics.IncrementFailed();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Materialization of outbox record {EventId} failed (worker={WorkerId}).",
                        record.EventId, workerId);
                    _metrics.IncrementFailed();

                    try
                    {
                        var marked = await outboxStore.MarkFailedAsync(
                            record.EventId,
                            record.LeaseToken,
                            $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None)
                            .ConfigureAwait(false);
                        // store 根据 retry_count 决定回退 Pending 或转 DeadLettered。
                        // 如果转为 DeadLettered，递增死信计数（通过 metrics updater 周期同步）。
                        // P0-8：若 lease 已被抢占（marked=false），跳过 MarkFailed——新 worker 会重新处理。
                        if (!marked)
                        {
                            _logger.LogWarning(
                                "Outbox record {EventId} MarkFailed 失败：lease 已被其他 worker 抢占（worker={WorkerId}）。",
                                record.EventId, workerId);
                        }
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogError(markEx,
                            "Failed to mark outbox record {EventId} as failed after materialization exception.",
                            record.EventId);
                    }
                }
                finally
                {
                    _metrics.DecrementProcessing();
                    heartbeatCts.Cancel();
                    try { await heartbeatTask.ConfigureAwait(false); }
                    catch { /* heartbeat 异常已在内部记录 */ }
                    heartbeatCts.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Learning materialization worker {WorkerId} crashed.", workerId);
        }
    }

    /// <summary>后台 heartbeat 续约（防止长物化任务租约过期）。</summary>
    /// <param name="leaseToken">当前 worker 持有的 lease token；与 store CAS 校验仅持有者能续约。</param>
    private static async Task RunHeartbeatAsync(
        ILearningEventOutboxStore outboxStore,
        string eventId,
        string leaseToken,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        CancellationTokenSource leaseCts)
    {
        while (!leaseCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatInterval, leaseCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (leaseCts.IsCancellationRequested) return;

            try
            {
                var renewed = await outboxStore.RenewLeaseAsync(eventId, leaseToken, leaseDuration, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!renewed)
                {
                    leaseCts.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (leaseCts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // 瞬时错误——等待下次续约。
            }
        }
    }

    /// <summary>周期性从 outbox store 同步指标（pending/processing/acked/dead_letter + last_success_at）。</summary>
    private async Task RunMetricsUpdaterAsync(
        ILearningEventOutboxStore outboxStore,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var stateCounts = await outboxStore.CountByStateAsync(cancellationToken).ConfigureAwait(false);
                _metrics.UpdateFromStateCounts(stateCounts);

                var lastSuccess = await outboxStore.GetLastSuccessAtAsync(cancellationToken).ConfigureAwait(false);
                _metrics.UpdateLastSuccessAt(lastSuccess);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update learning materialization metrics from outbox store.");
            }
        }
    }

    private static string GenerateOwnerId()
    {
        try
        {
            var machine = Environment.MachineName;
            var pid = Environment.ProcessId;
            var guid = Guid.NewGuid().ToString("N").Substring(0, 12);
            var raw = $"learn-{machine}-p{pid}-{guid}";
            return raw.Length > 60 ? raw.Substring(0, 60) : raw;
        }
        catch
        {
            return $"learn-{Guid.NewGuid():N}";
        }
    }

    /// <summary>
    /// P0-9：Channel 传递的队列项——携带 record 与已在 Acquire 后启动的 heartbeat 任务。
    /// </summary>
    /// <remarks>
    /// 设计动机：原实现中 worker 从 Channel 取出 record 后才启动 heartbeat，导致排队期间的 record
    /// 可能因 lease 过期被其他 worker 抢占。此类型将 heartbeat 任务的 CTS 与 Task 与 record 绑定，
    /// Acquire 后立即启动 heartbeat，worker 接管后复用——排队期间 lease 也会被续约。
    /// </remarks>
    private sealed record LearningMaterializationQueueItem(
        LearningEventOutboxRecord Record,
        CancellationTokenSource HeartbeatCts,
        Task HeartbeatTask);
}
