using System.Collections.Concurrent;
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

    // 批量 heartbeat 协调器——单个后台任务为所有活跃 lease 续约，替代每 record 一个 heartbeat 任务。
    // eventId → (leaseToken, per-record CTS for signaling lease loss to the owning worker)
    private readonly ConcurrentDictionary<string, (string LeaseToken, CancellationTokenSource Cts, DateTimeOffset ConfirmedExpiresAt)> _activeLeases = new();
    private Task? _heartbeatCoordinatorTask;

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
        // Channel 携带 (record, leaseCts) 对——Acquire 后立即在 _activeLeases 注册，
        // 批量 heartbeat 协调器周期性续约，排队期间 lease 也会被续约，
        // 避免 record 在 Channel 中停留超过 leaseDuration 被抢占。
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

        // 启动批量 heartbeat 协调器——单个后台任务为所有活跃 lease 续约。
        _heartbeatCoordinatorTask = RunHeartbeatCoordinatorAsync(probeOutbox, leaseDuration, heartbeatInterval, stoppingToken);

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

            // 等待 heartbeat 协调器退出（stoppingToken 取消后协调器自然退出）。
            try { if (_heartbeatCoordinatorTask is not null) await _heartbeatCoordinatorTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { /* heartbeat coordinator 超时/异常忽略 */ }

            // 清理所有残余的活跃 lease（Channel 中未被 worker 消费的 item）。
            foreach (var kv in _activeLeases)
            {
                if (_activeLeases.TryRemove(kv.Key, out var entry))
                {
                    try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    try { entry.Cts.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            try { await metricsTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { /* metrics task 超时忽略 */ }

            _logger.LogInformation("Learning materialization worker stopped.");
        }
    }

    /// <summary>
    /// 从 outbox 拉取一批 pending 记录并写入 bounded Channel。
    /// 每个 record 入 Channel 前在 _activeLeases 注册——批量 heartbeat 协调器周期性续约，
    /// 排队期间 lease 也会被续约，避免被其他 worker 抢占。
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

            // Acquire 后立即在 _activeLeases 注册——批量 heartbeat 协调器会周期性续约。
            // leaseCts 由协调器在检测到租约丢失时 cancel，信号 worker 中止该 record 处理。
            // 如果 record 未被消费（Channel 关闭/异常），下面的 try-catch 会调用 RemoveLease 清理。
            var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // 本地记录 DB 确认的 ExpiresAt——AcquirePendingAsync 的 RETURNING 返回数据库写入的实际到期时间，
            // 不重新以应用时钟估算（避免应用时钟与 DB clock_timestamp() 偏差导致 watchdog 误判）。
            // 续约异常时不更新；DB 不可达超过 LeaseDuration 后过期 → watchdog cancel leaseCts → linked CTS 取消 MaterializeAsync。
            var confirmedExpiresAt = record.LeaseExpiresAt ?? DateTimeOffset.UtcNow.Add(leaseDuration);
            _activeLeases[record.EventId] = (record.LeaseToken ?? string.Empty, leaseCts, confirmedExpiresAt);

            try
            {
                await writer.WriteAsync(new LearningMaterializationQueueItem(record, leaseCts), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Channel 已关闭——移除 lease 并清理 CTS 防止泄漏。
                RemoveLease(record.EventId);
                break;
            }
        }
    }

    /// <summary>
    /// 固定 worker：从 Channel 读取 outbox 记录，反序列化 payload，调用 MaterializeAsync，Ack/DeadLetter。
    /// leaseCts 已在 <see cref="PollAndDispatchAsync"/> 中创建并注册到 _activeLeases——
    /// worker 复用 queueItem 中的 leaseCts，处理完成或异常时通过 RemoveLease 清理。
    /// 协调器检测到租约丢失时 cancel leaseCts，worker 捕获 OCE 中止处理。
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
                    // 主循环取消——从 _activeLeases 移除并清理 leaseCts 防止泄漏。
                    RemoveLease(queueItem.Record.EventId);
                    break;
                }

                var record = queueItem.Record;
                // 复用 PollAndDispatchAsync 已创建并注册的 leaseCts——
                // 协调器检测到租约丢失时 cancel leaseCts 信号 worker 中止。
                var leaseCts = queueItem.LeaseCts;
                // 创建组合 linked token —— lease 丢失时取消 MaterializeAsync，避免物化继续执行。
                // leaseCts 已链接 cancellationToken（workerCts.Token → stoppingToken），
                // 组合后：stoppingToken / workerCts / leaseCts 任一取消 → linkedCts 取消。
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseCts.Token);
                var linkedToken = linkedCts.Token;


                _metrics.IncrementProcessing();

                try
                {
                    // 反序列化 payload。
                    var decision = JsonSerializer.Deserialize<ContextDecisionResult>(record.Payload, PayloadSerializerOptions);
                    if (decision is null)
                    {
                        await outboxStore.MarkFailedAsync(record.EventId, record.LeaseToken ?? string.Empty, "Failed to deserialize payload: null result.", cancellationToken)
                            .ConfigureAwait(false);
                        _metrics.IncrementFailed();
                        continue;
                    }

                    // 调用 MaterializeAsync。
                    await materializer.MaterializeAsync(
                        decision, record.WorkspaceId, record.CollectionId, linkedToken)
                        .ConfigureAwait(false);

                    // Ack（需 lease_token 匹配——若 lease 已被抢占则 acked=false，当前 worker 应放弃该记录）。
                    var acked = await outboxStore.MarkAckedAsync(record.EventId, record.LeaseToken ?? string.Empty, linkedToken).ConfigureAwait(false);
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
                catch (OperationCanceledException) when (leaseCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
                            record.LeaseToken ?? string.Empty,
                            $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None)
                            .ConfigureAwait(false);
                        // store 根据 retry_count 决定回退 Pending 或转 DeadLettered。
                        // 如果转为 DeadLettered，递增死信计数（通过 metrics updater 周期同步）。
                        // 若 lease 已被抢占（marked=false），跳过 MarkFailed——新 worker 会重新处理。
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
                    // 从 _activeLeases 移除并清理 leaseCts——协调器不再续约此 record。
                    RemoveLease(record.EventId);
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

    /// <summary>
    /// 批量 heartbeat 协调器：单个后台任务周期性为所有活跃 lease 续约，替代每 record 一个 heartbeat 任务。
    /// 续约失败的 record（不在 renewed set 中）→ 租约丢失 → cancel 该 record 的 CTS 信号 worker 中止。
    /// </summary>
    private async Task RunHeartbeatCoordinatorAsync(
        ILearningEventOutboxStore outboxStore,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 快照当前所有活跃 lease——避免在 RenewLeaseBatchAsync 期间持有锁。
            var snapshot = _activeLeases.ToArray();
            if (snapshot.Length == 0) continue;

            var now = DateTimeOffset.UtcNow;

            // 本地 watchdog：内存比较，无 DB 开销。
            // 续约异常时不更新 ConfirmedExpiresAt；DB 不可达超过 LeaseDuration 后 ConfirmedExpiresAt 过期，
            // 立即 cancel leaseCts → worker 的 linked CTS 取消 MaterializeAsync，避免旧 Worker 越权继续物化。
            // 此检查先于 RenewLeaseBatchAsync——即使续约调用阻塞，已过期的 lease 也能被及时取消。
            foreach (var kv in snapshot)
            {
                if (now >= kv.Value.ConfirmedExpiresAt)
                {
                    try { kv.Value.Cts.Cancel(); }
                    catch (ObjectDisposedException) { /* CTS 已被 worker 清理——忽略 */ }
                }
            }

            // 仅续约尚未过期（未取消）的 lease——过期的 lease DB 端也会拒绝（lease_expires_at > clock_timestamp()）。
            var renewals = new List<(string EventId, string LeaseToken)>(snapshot.Length);
            foreach (var kv in snapshot)
            {
                if (now < kv.Value.ConfirmedExpiresAt)
                {
                    renewals.Add((kv.Key, kv.Value.LeaseToken));
                }
            }
            if (renewals.Count == 0) continue;

            IReadOnlyDictionary<string, DateTimeOffset> renewed;
            try
            {
                renewed = await outboxStore.RenewLeaseBatchAsync(renewals, leaseDuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 瞬时错误——不更新 ConfirmedExpiresAt（保持上一次确认的值），等待下次续约。
                // 本地 watchdog 已在上面检查过期，过期的 lease 会在后续迭代被取消。
                _logger.LogDebug(ex, "Failed to renew learning event outbox leases in batch.");
                continue;
            }

            // 续约成功——以数据库 RETURNING 返回的新 LeaseExpiresAt 更新本地确认期限（不重新以应用时钟估算）；
            // 未续约且未过期的（lease 被抢占）→ cancel 该 record 的 CTS。
            foreach (var kv in snapshot)
            {
                if (renewed.TryGetValue(kv.Key, out var confirmedExpiresAt))
                {
                    // 续约成功——更新 ConfirmedExpiresAt 为数据库确认的过期时间。
                    if (_activeLeases.TryGetValue(kv.Key, out var current))
                    {
                        _activeLeases[kv.Key] = (current.LeaseToken, current.Cts, confirmedExpiresAt);
                    }
                }
                else if (now < kv.Value.ConfirmedExpiresAt)
                {
                    // 未过期但续约失败 → lease 已被其他 worker 抢占 → cancel 信号 worker 中止。
                    try { kv.Value.Cts.Cancel(); }
                    catch (ObjectDisposedException) { /* CTS 已被 worker 清理——忽略 */ }
                }
            }
        }
    }

    /// <summary>
    /// 从 _activeLeases 移除指定 eventId 并清理其 CTS。
    /// 由 worker 在处理完成/异常时调用，防止协调器续约已完成的 record。
    /// </summary>
    private void RemoveLease(string eventId)
    {
        if (_activeLeases.TryRemove(eventId, out var entry))
        {
            try { entry.Cts.Cancel(); } catch (ObjectDisposedException) { }
            try { entry.Cts.Dispose(); } catch (ObjectDisposedException) { }
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
    /// Channel 传递的队列项——携带 record 与已在 Acquire 后创建的 leaseCts。
    /// </summary>
    /// <remarks>
    /// 设计动机：原实现中 worker 从 Channel 取出 record 后才启动 heartbeat，导致排队期间的 record
    /// 可能因 lease 过期被其他 worker 抢占。此类型将 lease CTS 与 record 绑定，
    /// Acquire 后立即注册到 _activeLeases 由批量协调器续约，worker 接管后复用——排队期间 lease 也会被续约。
    /// </remarks>
    private sealed record LearningMaterializationQueueItem(
        LearningEventOutboxRecord Record,
        CancellationTokenSource LeaseCts);
}
