using ContextCore.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Decision Commit Durable Outbox 后台 worker（WP-F 接线）：
/// 轮询 <see cref="IDecisionCommitOutbox"/> 中待处理条目，执行
/// "决策记录落库（Decision Evidence Plane durable 归档）" 并 Ack——
/// 决策产生点只做一次 durable 入队，记录落库失败 / 进程崩溃后由本 worker 重放，
/// Decision Record + Evidence 引用 + 物化意图经 outbox 连成可靠链。
/// </summary>
/// <remarks>
/// 仅 Postgres provider 时激活（IDecisionCommitOutbox 已注册）；
/// FileSystem / InMemory provider 时探测到 null 立即退出（决策记录由产生点直接落库的
/// 轻路径不在此处处理，语义与 Postgres 路径一致：记录最终持久化）。
/// </remarks>
public sealed class DecisionCommitWorker : BackgroundService
{
    /// <summary>每批次最多领取数。</summary>
    private const int BatchSize = 20;

    /// <summary>领取租约时长（过期后其他节点可接管重试）。</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>轮询间隔（无积压时）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _services;
    private readonly ILogger<DecisionCommitWorker> _logger;
    private readonly string _owner;
    private readonly TimeSpan _pollInterval;

    public DecisionCommitWorker(
        IServiceProvider services,
        ContextCoreRuntimeOptions options,
        ILogger<DecisionCommitWorker> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollInterval = options?.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : PollInterval;
        _owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var probeScope = _services.CreateScope();
        var outbox = probeScope.ServiceProvider.GetService<IDecisionCommitOutbox>();
        if (outbox is null)
        {
            _logger.LogInformation(
                "DecisionCommitWorker 检测到未注册 IDecisionCommitOutbox（非 Postgres provider），自退出。");
            return;
        }

        _logger.LogInformation("DecisionCommitWorker 启动：消费决策提交 outbox（记录落库 + 可靠链）。");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hasMore = false;
                try
                {
                    hasMore = await SettleOnceAsync(outbox, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DecisionCommitWorker 轮询异常（不中断后续轮询）。");
                }

                if (hasMore)
                {
                    // 满批：还有积压，立即续扫（吞吐优先）。
                    continue;
                }

                try
                {
                    await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _logger.LogInformation("DecisionCommitWorker 已停止。");
        }
    }

    /// <summary>单轮消费：领取一批 → 决策记录落库 → Ack / MarkFailed。返回 true = 本批取满。</summary>
    private async Task<bool> SettleOnceAsync(IDecisionCommitOutbox outbox, CancellationToken ct)
    {
        var claimed = await outbox.AcquirePendingAsync(BatchSize, _owner, LeaseDuration, ct).ConfigureAwait(false);
        foreach (var commit in claimed)
        {
            try
            {
                await PersistCommitAsync(commit, ct).ConfigureAwait(false);
                var acked = await outbox.AckAsync(commit.OutboxId, commit.LeaseToken!, ct).ConfigureAwait(false);
                if (!acked)
                {
                    _logger.LogWarning(
                        "决策提交 Ack 失败（租约被抢占）：decision_id={DecisionId}。",
                        commit.DecisionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "决策提交处理失败（待重试）：decision_id={DecisionId}。",
                    commit.DecisionId);
                try
                {
                    await outbox.MarkFailedAsync(commit.OutboxId, commit.LeaseToken!, ex.Message, ct).ConfigureAwait(false);
                }
                catch (Exception markEx)
                {
                    _logger.LogWarning(markEx, "标记决策提交失败异常（租约可能已被抢占）。");
                }
            }
        }

        return claimed.Count >= BatchSize;
    }

    /// <summary>执行决策提交：决策记录落库（Decision Evidence Plane durable 归档）。</summary>
    private async Task PersistCommitAsync(DecisionCommitOutboxRecord commit, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var decisionTraceStore = scope.ServiceProvider.GetService<IDecisionTraceStore>();
        if (decisionTraceStore is null)
        {
            // 未注册决策记录存储（异常组合）：记录无法落库 → 抛异常转 MarkFailed（重试/死信）。
            throw new InvalidOperationException(
                $"IDecisionTraceStore 未注册，无法归档决策记录：decision_id={commit.DecisionId}。");
        }

        await decisionTraceStore.SaveAsync(commit.Record, ct).ConfigureAwait(false);
    }
}
