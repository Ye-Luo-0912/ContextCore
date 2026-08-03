using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// Learning Loop 物化调度器。替代 <c>Task.Run → MaterializeAsync → catch {}</c> 模式。
/// </summary>
/// <remarks>
/// <para>
/// 两种路径（自动选择）：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Durable Outbox（Postgres）</b>：<see cref="EnqueueAsync"/> 将 ContextDecisionResult 序列化为 JSON
/// 并写入 <c>learning_event_outbox</c> 表（持久化）。独立的 <c>LearningMaterializationWorker</c>
/// （BackgroundService）轮询 outbox 表，调用 <see cref="UtilityLedgerMaterializer.MaterializeAsync"/>，
/// 然后 Ack / Retry / DeadLetter。进程崩溃时不丢数据（outbox 行在 DB 中持久化）。
/// </item>
/// <item>
/// <b>In-Memory Channel（FileSystem / InMemory）</b>：<see cref="EnqueueAsync"/> 将 ContextDecisionResult
/// 写入 bounded Channel（容量受 <see cref="LearningMaterializationOptions.ChannelCapacity"/> 限制）。
/// 固定数量的 worker 任务从 Channel 读取并调用 <see cref="UtilityLedgerMaterializer.MaterializeAsync"/>。
/// 消除每请求 <c>Task.Run</c>，提供背压与队列深度管理。非持久——进程崩溃时 Channel 中未处理的数据丢失
/// （与原 Task.Run 行为一致，但消除了 Task 风暴）。
/// </item>
/// </list>
/// <para>
/// 优雅关闭：<see cref="StopAsync"/> 信号 Channel 完成，等待固定 worker 排空当前队列（最多 30 秒）。
/// </para>
/// </remarks>
public sealed class LearningMaterializationDispatcher : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UtilityLedgerMaterializer? _materializer;
    private readonly ILearningEventOutboxStore? _outboxStore;
    private readonly LearningMaterializationOptions _options;
    private readonly LearningMaterializationMetrics _metrics;
    private readonly ILogger? _logger;

    private readonly Channel<LearningMaterializationWorkItem>? _channel;
    private Task[]? _workers;
    private CancellationTokenSource? _workerCts;
    private readonly bool _useOutbox;

    /// <summary>
    /// 构造调度器。
    /// </summary>
    /// <param name="materializer">Utility Ledger 物化器（null = 不物化，仅记录指标）。</param>
    /// <param name="outboxStore">Learning Event Outbox 存储（null = 非 Postgres，走 in-memory Channel）。</param>
    /// <param name="options">配置。</param>
    /// <param name="metrics">指标收集器。</param>
    /// <param name="logger">日志（null = 静默）。</param>
    public LearningMaterializationDispatcher(
        UtilityLedgerMaterializer? materializer,
        ILearningEventOutboxStore? outboxStore = null,
        LearningMaterializationOptions? options = null,
        LearningMaterializationMetrics? metrics = null,
        ILogger<LearningMaterializationDispatcher>? logger = null)
    {
        _materializer = materializer;
        _outboxStore = outboxStore;
        _options = options ?? new LearningMaterializationOptions();
        _metrics = metrics ?? new LearningMaterializationMetrics();
        _logger = logger;

        _useOutbox = outboxStore is not null;

        if (!_useOutbox && materializer is not null)
        {
            // 非 Postgres 路径：创建 bounded Channel 供固定 worker 消费。
            var capacity = Math.Max(1, _options.ChannelCapacity);
            _channel = Channel.CreateBounded<LearningMaterializationWorkItem>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                });
        }
    }

    /// <summary>
    /// 入队一次 Decision 物化事件。替代原 Task.Run fire-and-forget。
    /// </summary>
    /// <param name="decision">决策结果（含 SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <param name="workspaceId">workspace 作用域。</param>
    /// <param name="collectionId">collection 作用域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnqueueAsync(
        ContextDecisionResult decision,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // 无 materializer 且无 outbox：直接跳过（开发/测试路径未注入）。
        if (_materializer is null && _outboxStore is null)
        {
            return;
        }

        if (_useOutbox)
        {
            await EnqueueToOutboxAsync(decision, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }
        else if (_channel is not null)
        {
            await EnqueueToChannelAsync(decision, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 入队一次 Decision 物化事件并<b>等待持久化完成</b>（Durable Outbox 路径下等待 PostgreSQL INSERT 完成）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="EnqueueBestEffortAsync"/> 的关键差异：
    /// </para>
    /// <list type="bullet">
    /// <item><b>失败必须抛出异常</b>——Outbox INSERT 失败时不调用 <c>FallbackDirectMaterialize</c>，
    /// 直接将异常向上抛给调用方，让上层决策流感知到 Learning Event 持久化失败（避免名义"持久"
    /// 实际"best-effort"的语义不一致）。</item>
    /// <item>Durable Outbox 路径：等待 <c>learning_event_outbox</c> 表 INSERT 完成才返回，
    /// 保证调用方返回前 Learning Event 已持久化到 PostgreSQL——进程退出/崩溃时不丢数据。
    /// 仅等待入队持久化，不等待后续 Materialize（worker 异步消费）。</item>
    /// <item>In-Memory Channel 路径：等待 Channel.Writer.WriteAsync 完成（非持久，进程崩溃会丢失；
    /// 与原 Task.Run 行为一致）。Channel 关闭时抛 <see cref="ChannelClosedException"/>。</item>
    /// </list>
    /// <para>
    /// 主决策路径必须使用此方法而非 fire-and-forget <c>_ = dispatcher.EnqueueBestEffortAsync(...)</c>，
    /// 否则进程可能在 EnqueueAsync 完成 PostgreSQL INSERT 前退出，导致 Learning Event 丢失。
    /// </para>
    /// </remarks>
    /// <param name="decision">决策结果（含 SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <param name="workspaceId">workspace 作用域。</param>
    /// <param name="collectionId">collection 作用域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">_outboxStore 与 _materializer 均未注入（无路径可走）。</exception>
    /// <exception cref="Exception">Outbox INSERT 或 Channel 写入失败时原样向上抛出（不降级）。</exception>
    public async Task EnqueueDurablyAsync(
        ContextDecisionResult decision,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // 无 materializer 且无 outbox：直接跳过（开发/测试路径未注入）。
        if (_materializer is null && _outboxStore is null)
        {
            return;
        }

        if (_useOutbox)
        {
            // Durable 路径——直接调用 _outboxStore.EnqueueAsync 并让其异常向上抛，
            // 不走 EnqueueToOutboxAsync 的 try-catch fallback 语义。确保返回前 PostgreSQL 已确认写入。
            await EnqueueToOutboxDurablyAsync(decision, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (_channel is not null)
        {
            // Channel 路径——await WriteAsync 完成，ChannelClosedException 向上抛（不降级）。
            var workItem = new LearningMaterializationWorkItem(
                decision, workspaceId, collectionId, DateTimeOffset.UtcNow);
            await _channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
            _metrics.IncrementPending();
        }
    }

    /// <summary>
    /// 入队一次 Decision 物化事件（best-effort，失败不抛出）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="EnqueueDurablyAsync"/> 的关键差异：
    /// </para>
    /// <list type="bullet">
    /// <item><b>允许 <c>FallbackDirectMaterialize</c></b>——Outbox INSERT 失败时降级到直接物化，
    /// 直接物化再失败也只记录日志不向调用方抛出。</item>
    /// <item>语义与原 <see cref="EnqueueAsync"/> 完全一致——保留向后兼容路径。</item>
    /// </list>
    /// <para>
    /// 适用于非关键路径（如后台导入、批处理重建）——Learning Event 丢失可接受、不能影响主流程。
    /// 主决策路径必须使用 <see cref="EnqueueDurablyAsync"/>。
    /// </para>
    /// </remarks>
    /// <param name="decision">决策结果（含 SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <param name="workspaceId">workspace 作用域。</param>
    /// <param name="collectionId">collection 作用域。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnqueueBestEffortAsync(
        ContextDecisionResult decision,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        // 与原 EnqueueAsync 行为完全一致——内部 try-catch 降级 + FallbackDirectMaterialize。
        await EnqueueAsync(decision, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Durable 路径专用——直接调用 outbox store 持久化，失败时异常向上抛（不降级、不 fallback）。
    /// 与 <see cref="EnqueueToOutboxAsync"/> 的差异：异常向上抛而非调用 <see cref="FallbackDirectMaterializeAsync"/>。
    /// </summary>
    private async Task EnqueueToOutboxDurablyAsync(
        ContextDecisionResult decision,
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(decision, PayloadSerializerOptions);
        var record = new LearningEventOutboxRecord
        {
            EventId = "learn-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId ?? string.Empty,
            CollectionId = collectionId ?? string.Empty,
            DecisionId = decision.RequestId,
            Payload = payload,
            State = LearningEventOutboxStates.Pending,
            MaxRetryCount = _options.MaxRetryCount,
            CreatedAt = now,
            UpdatedAt = now
        };

        // 失败直接抛出——_outboxStore.EnqueueAsync 内部已包含事务 Commit，
        // Commit 成功才返回；任何异常都意味着 PostgreSQL 未确认写入，必须让调用方感知。
        await _outboxStore!.EnqueueAsync(record, null, cancellationToken).ConfigureAwait(false);
        _metrics.IncrementPending();
    }

    /// <summary>Durable Outbox 路径：序列化 decision 并写入 outbox 表。</summary>
    private async Task EnqueueToOutboxAsync(
        ContextDecisionResult decision,
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(decision, PayloadSerializerOptions);
        var record = new LearningEventOutboxRecord
        {
            EventId = "learn-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId ?? string.Empty,
            CollectionId = collectionId ?? string.Empty,
            DecisionId = decision.RequestId,
            Payload = payload,
            State = LearningEventOutboxStates.Pending,
            MaxRetryCount = _options.MaxRetryCount,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await _outboxStore!.EnqueueAsync(record, null, cancellationToken).ConfigureAwait(false);
            _metrics.IncrementPending();
        }
        catch (Exception ex)
        {
            // outbox 写入失败 — 降级到直接物化（best-effort，避免数据完全丢失）。
            _logger?.LogWarning(ex,
                "Failed to enqueue learning event to outbox (decision={DecisionId}). Falling back to direct materialization.",
                decision.RequestId);
            await FallbackDirectMaterializeAsync(decision, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>In-Memory Channel 路径：写入 bounded Channel。</summary>
    private async Task EnqueueToChannelAsync(
        ContextDecisionResult decision,
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var workItem = new LearningMaterializationWorkItem(
            decision, workspaceId, collectionId, DateTimeOffset.UtcNow);

        try
        {
            await _channel!.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
            _metrics.IncrementPending();
        }
        catch (ChannelClosedException)
        {
            // Channel 已关闭（服务关闭中）— 降级到同步物化。
            await FallbackDirectMaterializeAsync(decision, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>降级路径：直接调用 MaterializeAsync（best-effort，非持久）。</summary>
    private async Task FallbackDirectMaterializeAsync(
        ContextDecisionResult decision,
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (_materializer is null) return;
        try
        {
            await _materializer.MaterializeAsync(decision, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            _metrics.RecordSuccess();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Fallback direct materialization failed (decision={DecisionId}).", decision.RequestId);
            _metrics.IncrementFailed();
        }
    }

    // ── IHostedService ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_channel is null || _materializer is null)
        {
            return Task.CompletedTask;
        }

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerCount = Math.Max(1, _options.WorkerCount);
        _workers = new Task[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i;
            _workers[i] = RunWorkerAsync(workerId, _workerCts.Token);
        }

        _logger?.LogInformation(
            "LearningMaterializationDispatcher started {WorkerCount} in-memory workers (channel capacity={Capacity}).",
            workerCount, _options.ChannelCapacity);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is null || _workers is null)
        {
            return;
        }

        // 信号 Channel 不再接受新写入，让 worker 排空剩余项。
        _channel.Writer.TryComplete();

        try
        {
            // 等待 worker 排空（最多 30 秒）。
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drainCts.CancelAfter(TimeSpan.FromSeconds(30));
            await Task.WhenAll(_workers).WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 排空超时 — 强制取消 worker。
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during LearningMaterializationDispatcher drain.");
        }
        finally
        {
            _workerCts?.Cancel();
        }

        _logger?.LogInformation("LearningMaterializationDispatcher stopped.");
    }

    /// <summary>固定 worker 循环：从 Channel 读取 work item，调用 MaterializeAsync。</summary>
    private async Task RunWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        var reader = _channel!.Reader;

        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _metrics.DecrementPending();
                _metrics.IncrementProcessing();

                try
                {
                    await _materializer!.MaterializeAsync(item.Decision, item.WorkspaceId, item.CollectionId, cancellationToken)
                        .ConfigureAwait(false);

                    var lagMs = (DateTimeOffset.UtcNow - item.CreatedAt).TotalMilliseconds;
                    _metrics.RecordMaterializationLag(lagMs);
                    _metrics.RecordSuccess();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "In-memory materialization failed (worker={WorkerId}, decision={DecisionId}).",
                        workerId, item.Decision.RequestId);
                    _metrics.IncrementFailed();
                }
                finally
                {
                    _metrics.DecrementProcessing();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (ChannelClosedException)
        {
            // Channel 已关闭 — 正常退出。
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LearningMaterializationDispatcher worker {WorkerId} crashed.", workerId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_workerCts is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _workerCts.Dispose();
            // 幂等：本类同时以 Singleton 与 HostedService 双注册，DI 容器可能对同一实例
            // 触发两次 DisposeAsync；置 null 后第二次 StopAsync 不再触碰已释放的 CTS。
            _workerCts = null;
        }
    }

    /// <summary>内部 work item（非 Postgres 路径使用）。</summary>
    private sealed record LearningMaterializationWorkItem(
        ContextDecisionResult Decision,
        string? WorkspaceId,
        string? CollectionId,
        DateTimeOffset CreatedAt);
}
