using ContextCore.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Agent Run 事件流自动压缩后台 worker。
/// </summary>
/// <remarks>
/// 周期性扫描热表事件数达到阈值（<see cref="AgentRunEventCompactionOptions.MinEventCount"/>）的 Run，
/// 逐个折叠到当前最后事件：锚点保留在热表维持哈希链，前缀归档到
/// <c>agent_run_events_archive</c> 后删除——控制长生命周期 Run 热表无界增长。
///
/// <b>阈值策略</b>：每轮仅处理事件数 ≥ 阈值的候选（按事件数降序，限量
/// <see cref="AgentRunEventCompactionOptions.MaxRunsPerPass"/>）；压缩后热表只剩锚点，
/// 需再次积累到阈值才会被重新选中，天然限流。
///
/// <b>多实例（HA）</b>：<see cref="IAgentRunEventCompactor.CompactAsync"/> 幂等，
/// 多实例并发压缩同一 Run 的冲突事务由 Postgres 中止；单 Run 失败不影响本轮其他 Run，
/// 下轮自动重试。
///
/// 非 Postgres provider（<see cref="IAgentRunEventCompactor"/> 未注册）或
/// <see cref="AgentRunEventCompactionOptions.Enabled"/>=false 时自退出。
/// </remarks>
internal sealed class AgentRunEventCompactionWorker : BackgroundService
{
    private readonly IAgentRunEventCompactor? _compactor;
    private readonly IOptionsMonitor<AgentRunEventCompactionOptions> _options;
    private readonly ILogger<AgentRunEventCompactionWorker> _logger;

    public AgentRunEventCompactionWorker(
        IAgentRunEventCompactor? compactor,
        IOptionsMonitor<AgentRunEventCompactionOptions> options,
        ILogger<AgentRunEventCompactionWorker> logger)
    {
        _compactor = compactor;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_compactor is null)
        {
            _logger.LogInformation(
                "AgentRunEventCompactionWorker 已禁用（IAgentRunEventCompactor 未注册，非 Postgres provider）。");
            return;
        }

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("AgentRunEventCompactionWorker 已禁用（EventCompaction:Enabled=false）。");
            return;
        }

        _logger.LogInformation(
            "AgentRunEventCompactionWorker 已启动：阈值 {MinEventCount} 事件 / 每轮最多 {MaxRunsPerPass} 个 Run。",
            options.MinEventCount, options.MaxRunsPerPass);

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = false;
            try
            {
                succeeded = await RunCompactionPassAsync(options, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentRunEventCompactionWorker 扫描候选 Run 异常。");
            }

            if (succeeded)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                _logger.LogWarning(
                    "AgentRunEventCompactionWorker 本轮存在失败项（连续失败 {ConsecutiveFailures} 次），进入退避。",
                    consecutiveFailures);
            }

            var delay = consecutiveFailures == 0
                ? options.PollInterval
                : WorkerBackoff.Compute(
                    options.PollInterval, options.BackoffBaseDelay, options.BackoffMaxDelay,
                    options.MaxRetryCount, consecutiveFailures);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 执行一轮压缩：扫描候选 Run 并逐个折叠到其当前最后事件。
    /// 返回 false 表示本轮存在失败项（由调用方驱动退避重试）。
    /// </summary>
    private async Task<bool> RunCompactionPassAsync(AgentRunEventCompactionOptions options, CancellationToken ct)
    {
        var candidates = await _compactor!
            .FindCandidatesAsync(options.MinEventCount, options.MaxRunsPerPass, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return true;
        }

        var anyFailed = false;
        foreach (var candidate in candidates)
        {
            try
            {
                var result = await _compactor
                    .CompactAsync(candidate.WorkspaceId, candidate.RunId, candidate.LastSequence, ct)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Run {RunId} 事件流自动压缩完成：锚点 {AnchorSequence}，折叠 {FoldedEventCount} 事件，归档 {ArchivedRowCount} 行。",
                    candidate.RunId, result.CompactedThroughSequence, result.FoldedEventCount, result.ArchivedRowCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                anyFailed = true;
                _logger.LogWarning(
                    ex, "Run {RunId}（Workspace {WorkspaceId}）事件流自动压缩失败，下轮重试。",
                    candidate.RunId, candidate.WorkspaceId);
            }
        }

        return !anyFailed;
    }
}

/// <summary>
/// Agent Run 事件流自动压缩配置选项。
/// </summary>
public sealed class AgentRunEventCompactionOptions
{
    /// <summary>是否启用自动压缩（非 Postgres provider 时即使为 true 也会因 compactor 缺失而自退出）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔（成功轮询后的正常等待时间）。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>触发压缩的热表事件数阈值（含）：Run 热表事件数 ≥ 该值时纳入候选。</summary>
    public int MinEventCount { get; set; } = 1000;

    /// <summary>每轮最多压缩的 Run 数（控制单轮负载上限）。</summary>
    public int MaxRunsPerPass { get; set; } = 20;

    /// <summary>失败退避基准延迟（连续失败时第 1 次重试的等待时间）。</summary>
    public TimeSpan BackoffBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>失败退避最大延迟（指数增长的上限，防止长时间无界等待）。</summary>
    public TimeSpan BackoffMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>退避指数上限（连续失败超过该次数后保持 BackoffMaxDelay，不继续指数增长）。</summary>
    public int MaxRetryCount { get; set; } = 8;
}
