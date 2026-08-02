using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// 关系写入 outbox 调度与 reconciliation 后台服务。
/// 周期性从 <see cref="IRelationOutboxStore"/> 取出 pending 记录，
/// 验证对应 relation 是否已落库；缺失则回放写入，已存在则标记 Applied。
/// </summary>
/// <remarks>
/// 优雅降级：
/// <list type="bullet">
/// <item>
/// 当 <see cref="IRelationOutboxStore"/> 未注册时（FileSystem / InMemory provider），
/// worker 启动后记录日志并立即返回——不执行任何调度。
/// 单进程 FileSystem 不需要 reconciliation（无并发写入者），故此路径是安全的 no-op。
/// </item>
/// <item>
/// 当 <see cref="IRelationStore"/> 未注册时同样退出——理论上不会发生（所有 provider 都注册此契约）。
/// </item>
/// </list>
/// <para>
/// Reconciliation 语义：
/// <list type="bullet">
/// <item>Upsert 记录：调用 <see cref="IRelationStore.GetAsync"/> 验证 relation 存在且关键字段匹配。
/// 若缺失或字段漂移则通过 <see cref="IRelationProjectionWriter.WriteAsync"/> 回放写入（走 validator + upsert），
/// 然后调用 <see cref="IRelationOutboxStore.MarkAppliedAsync"/>。</item>
/// <item>Delete 记录：当前 OutboxAwareRelationProjectionWriter 仅产生 Upsert 记录；
/// Delete 路径为未来扩展（如启用级联删除时通过 outbox 同步）。worker 处理 Delete 时验证 relation 不存在即可标记 Applied。</item>
/// <item>处理异常时调用 <see cref="IRelationOutboxStore.MarkFailedAsync"/>——
/// 由 store 根据 retry_count/max_retry_count 决定回退 Pending 或转 Failed。</item>
/// </list>
/// </para>
/// <para>
/// 租约与心跳：每条记录取出后持有 <see cref="RelationReconciliationOptions.LeaseDuration"/> 时长的租约。
/// 处理过程中按 <see cref="RelationReconciliationOptions.HeartbeatInterval"/> 周期调用
/// <see cref="IRelationOutboxStore.RenewHeartbeatAsync"/> 防止租约过期。
/// 续约失败（返回 false）时中止当前记录处理——其他 worker 可通过 AcquirePendingAsync 抢占。
/// </para>
/// </remarks>
public sealed class RelationReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<RelationReconciliationOptions> _options;
    private readonly ILogger<RelationReconciliationWorker> _logger;

    public RelationReconciliationWorker(
        IServiceProvider services,
        IOptions<RelationReconciliationOptions> options,
        ILogger<RelationReconciliationWorker> logger)
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
            _logger.LogInformation("Relation reconciliation worker is disabled.");
            return;
        }

        // 检测 IRelationOutboxStore 是否注册——未注册时退出（FileSystem/InMemory provider 优雅降级）。
        using var probeScope = _services.CreateScope();
        var probeOutbox = probeScope.ServiceProvider.GetService<IRelationOutboxStore>();
        if (probeOutbox is null)
        {
            _logger.LogInformation(
                "Relation reconciliation worker detected no IRelationOutboxStore registered " +
                "(FileSystem/InMemory provider). Worker will exit without scheduling. " +
                "Configure Storage:Provider=postgres to enable outbox-based reconciliation.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds));
        var batchSize = Math.Max(1, options.BatchSize);
        var leaseDuration = options.LeaseDuration;
        var heartbeatInterval = options.HeartbeatInterval;
        var owner = string.IsNullOrWhiteSpace(options.OwnerId)
            ? GenerateOwnerId()
            : options.OwnerId;

        _logger.LogInformation(
            "Relation reconciliation worker started. Interval={Interval}s, BatchSize={BatchSize}, Owner={Owner}, " +
            "LeaseDuration={LeaseDuration}, HeartbeatInterval={HeartbeatInterval}, RunOnStartup={RunOnStartup}.",
            interval.TotalSeconds, batchSize, owner, leaseDuration, heartbeatInterval, options.RunOnStartup);

        if (options.RunOnStartup)
        {
            await RunReconciliationBatchAsync(probeOutbox, batchSize, owner, leaseDuration, heartbeatInterval, stoppingToken)
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

            await RunReconciliationBatchAsync(probeOutbox, batchSize, owner, leaseDuration, heartbeatInterval, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunReconciliationBatchAsync(
        IRelationOutboxStore outboxStore,
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RelationOutboxRecord> batch;
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
            _logger.LogError(ex, "Failed to acquire pending outbox records.");
            return;
        }

        if (batch.Count == 0) return;

        _logger.LogInformation("Acquired {Count} outbox records for reconciliation.", batch.Count);

        using var scope = _services.CreateScope();
        var relationStore = scope.ServiceProvider.GetService<IRelationStore>();
        var projectionWriter = scope.ServiceProvider.GetService<IRelationProjectionWriter>();

        if (relationStore is null)
        {
            _logger.LogWarning(
                "IRelationStore unavailable — cannot reconcile {Count} outbox records. They will be retried after lease expiry.",
                batch.Count);
            return;
        }

        var appliedCount = 0;
        var replayedCount = 0;
        var failedCount = 0;
        var leaseLostCount = 0;
        foreach (var record in batch)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var (replayed, applied, leaseLost) = await ReconcileRecordAsync(
                    outboxStore, relationStore, projectionWriter, record, owner, leaseDuration, heartbeatInterval, cancellationToken)
                    .ConfigureAwait(false);

                if (leaseLost)
                {
                    leaseLostCount++;
                    // 租约丢失——保留 state='Dispatched'，lease_expires_at 已过期或即将过期，
                    // 其他 worker 通过 AcquirePendingAsync 抢占（Pending OR (Dispatched AND lease_expires_at <= now)）。
                    // 不调用 MarkApplied/MarkFailed——避免污染状态机。
                    _logger.LogWarning(
                        "Outbox record {OutboxId} (relation={RelationId}) aborted due to lease loss. " +
                        "Another worker may re-acquire after lease expiry.",
                        record.OutboxId, record.RelationId);
                    continue;
                }

                if (applied) appliedCount++;
                if (replayed) replayedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // host 关闭——剩余记录由其他 worker 通过 AcquirePendingAsync 抢占（lease 过期后）。
                return;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex,
                    "Reconciliation of outbox record {OutboxId} (relation={RelationId}) failed unexpectedly.",
                    record.OutboxId, record.RelationId);
                try
                {
                    await outboxStore.MarkFailedAsync(record.OutboxId, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx,
                        "Failed to mark outbox record {OutboxId} as failed after reconciliation exception.",
                        record.OutboxId);
                }
            }
        }

        _logger.LogInformation(
            "Reconciliation batch complete: applied={Applied}, replayed={Replayed}, failed={Failed}, leaseLost={LeaseLost}.",
            appliedCount, replayedCount, failedCount, leaseLostCount);
    }

    /// <summary>
    /// 处理单条 outbox 记录。返回 (Replayed, Applied, LeaseLost) 三元组：
    /// <list type="bullet">
    /// <item>LeaseLost=true：RenewHeartbeatAsync 返回 false，处理被取消；不调用 MarkApplied/MarkFailed。</item>
    /// <item>Applied=true：已成功 reconciliation（已落库匹配或回放成功），调用 MarkAppliedAsync。</item>
    /// <item>Replayed=true：通过 projectionWriter 回放了写入。</item>
    /// </list>
    /// </summary>
    private static async Task<(bool Replayed, bool Applied, bool LeaseLost)> ReconcileRecordAsync(
        IRelationOutboxStore outboxStore,
        IRelationStore relationStore,
        IRelationProjectionWriter? projectionWriter,
        RelationOutboxRecord record,
        string owner,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        // 启动 heartbeat 任务——续约失败时取消当前记录处理。
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatAsync(outboxStore, record.OutboxId, owner, leaseDuration, heartbeatInterval, leaseCts);

        try
        {
            if (record.OperationKind == RelationOutboxOperationKind.Upsert)
            {
                var (replayed, applied) = await ReconcileUpsertAsync(
                    outboxStore, relationStore, projectionWriter, record, leaseCts.Token)
                    .ConfigureAwait(false);
                return (replayed, applied, LeaseLost: false);
            }

            // Delete：验证 relation 已不存在则标记 Applied。
            var existing = await relationStore.GetAsync(
                record.WorkspaceId, record.CollectionId, record.RelationId, leaseCts.Token)
                .ConfigureAwait(false);
            if (existing is null)
            {
                await outboxStore.MarkAppliedAsync(record.OutboxId, leaseCts.Token).ConfigureAwait(false);
                return (false, true, false);
            }

            // relation 仍存在——可能是 delete 未生效或 outbox 记录语义错误。
            // 当前 OutboxAwareRelationProjectionWriter 不产生 Delete 记录，此分支仅作 forward-compat 兜底。
            await outboxStore.MarkFailedAsync(
                record.OutboxId,
                "Delete outbox record encountered existing relation; replay logic not implemented for delete.",
                leaseCts.Token).ConfigureAwait(false);
            return (false, false, false);
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // 租约丢失（heartbeat 续约失败取消了 leaseCts）——返回 LeaseLost=true，
            // 让上层不调用 MarkApplied/MarkFailed，保留 Dispatched 状态供其他 worker 抢占。
            return (false, false, true);
        }
        finally
        {
            leaseCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); }
            catch { /* heartbeat 异常已在内部记录 */ }
        }
    }

    private static async Task<(bool Replayed, bool Applied)> ReconcileUpsertAsync(
        IRelationOutboxStore outboxStore,
        IRelationStore relationStore,
        IRelationProjectionWriter? projectionWriter,
        RelationOutboxRecord record,
        CancellationToken cancellationToken)
    {
        var payload = record.Payload;
        if (payload is null)
        {
            // payload 缺失——无法回放。标记失败让 store 决定重试或终态。
            await outboxStore.MarkFailedAsync(record.OutboxId, "Outbox record payload is null; cannot replay upsert.", cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        // 验证 relation 是否已落库且关键字段匹配。
        var existing = await relationStore.GetAsync(
            record.WorkspaceId, record.CollectionId, record.RelationId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null && RelationMatches(existing, payload))
        {
            // 已落库且字段一致——标记 Applied。
            await outboxStore.MarkAppliedAsync(record.OutboxId, cancellationToken).ConfigureAwait(false);
            return (false, true);
        }

        // 缺失或字段漂移——回放写入。projectionWriter 为 null 时无法回放（理论上不会发生）。
        if (projectionWriter is null)
        {
            await outboxStore.MarkFailedAsync(
                record.OutboxId,
                "IRelationProjectionWriter unavailable; cannot replay upsert.",
                cancellationToken).ConfigureAwait(false);
            return (false, false);
        }

        // 回放走 validator + upsert——保持与原始写入路径一致。
        // provenance 使用 outbox 记录中的 Provenance 字段（与原始入队时一致）。
        await projectionWriter.WriteAsync(new[] { payload }, record.Provenance, cancellationToken)
            .ConfigureAwait(false);

        await outboxStore.MarkAppliedAsync(record.OutboxId, cancellationToken).ConfigureAwait(false);
        return (true, true);
    }

    /// <summary>
    /// 比较已落库 relation 与 outbox payload 的关键字段是否一致。
    /// 仅检查影响多跳评分的字段（weight / confidence）和标识字段——
    /// 完整 deep-equal 会因 UpdatedAt 等时间戳字段始终不等而误判。
    /// </summary>
    private static bool RelationMatches(ContextRelation existing, ContextRelation payload)
    {
        return string.Equals(existing.SourceId, payload.SourceId, StringComparison.Ordinal)
            && string.Equals(existing.TargetId, payload.TargetId, StringComparison.Ordinal)
            && string.Equals(existing.RelationType, payload.RelationType, StringComparison.Ordinal)
            && Math.Abs(existing.Weight - payload.Weight) < 1e-9
            && Math.Abs(existing.Confidence - payload.Confidence) < 1e-9;
    }

    private static async Task RunHeartbeatAsync(
        IRelationOutboxStore outboxStore,
        string outboxId,
        string owner,
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
                var renewed = await outboxStore.RenewHeartbeatAsync(outboxId, owner, leaseDuration, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!renewed)
                {
                    // 租约丢失——取消当前记录处理。其他 worker 会通过 AcquirePendingAsync 抢占。
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
                // 瞬时错误——等待下次续约。若多次失败导致 lease 过期，下次 RenewHeartbeatAsync 仍会返回 false。
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
            var raw = $"reconcile-{machine}-p{pid}-{guid}";
            return raw.Length > 60 ? raw.Substring(0, 60) : raw;
        }
        catch
        {
            return $"reconcile-{Guid.NewGuid():N}";
        }
    }
}
