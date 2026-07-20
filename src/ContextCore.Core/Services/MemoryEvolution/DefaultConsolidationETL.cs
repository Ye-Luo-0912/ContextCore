using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// R21-4：IConsolidationETL 的默认实现。把 superseded/replaced items 从 active store
/// 推进到 Replaced → Archived，通过 IMemoryStateStore 的事件流驱动状态迁移。
/// </summary>
/// <remarks>
/// 设计原则（对齐 R21-4 契约）：
///   1. 幂等：重复执行不会产生副作用（已 Archived 的 item 跳过）。
///   2. 可中断：批次大小限制单次处理量；调用方可循环执行直到 ExtractedCount=0。
///   3. 失败不破坏数据：Transform 写入 Replaced 事件后失败，
///      下次 ETL 从 Replaced 状态继续推进到 Archived。
///   4. 不直接修改 active store 中的 item；只通过事件流推进状态。
///      item 在 active store 中的实际删除由独立 GC 流程处理。
///   5. DryRun=true 仅返回预计处理数量，不写入任何事件。
///   6. 兼容衰减路径：Dormant → Archived 也可由 ETL 推进（彻底降权场景）。
/// </remarks>
public sealed class DefaultConsolidationETL : IConsolidationETL
{
    private readonly IMemoryStateStore _store;
    private readonly TimeProvider? _timeProvider;

    public DefaultConsolidationETL(IMemoryStateStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<ConsolidationRunResult> RunAsync(
        ConsolidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = "run-" + Guid.NewGuid().ToString("N");
        var startedAt = Now();

        // Extract：查询所有事件，按 SourceItemId 分组取最新状态。
        // 基于"最新状态"分类为待处理列表（Superseded / Replaced / Dormant 终极降权），
        // 确保已经被推进到 Replaced / Archived 的 item 不会被重复处理。
        var allEvents = await _store.QueryEventsAsync(new MemoryStateEventQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Until = request.OlderThan,
            Take = 0 // 不限制
        }, cancellationToken);

        // 按 SourceItemId 分组，取最新状态的事件
        var latestByItem = allEvents
            .GroupBy(e => e.SourceItemId)
            .Select(g => g.MaxBy(e => e.OccurredAt)!)
            .Where(e => e.NewState == MemoryState.Superseded
                || e.NewState == MemoryState.Replaced
                || e.NewState == MemoryState.Dormant)
            .ToList();

        // 按 ItemType 过滤（若指定）
        if (request.ItemTypes.Count > 0)
        {
            var allowedTypes = new HashSet<string>(request.ItemTypes, StringComparer.Ordinal);
            latestByItem = latestByItem
                .Where(e => allowedTypes.Contains(e.ItemType))
                .ToList();
        }

        // 分类为待处理列表
        var supersededToProcess = latestByItem
            .Where(e => e.NewState == MemoryState.Superseded)
            .OrderBy(e => e.OccurredAt)
            .Take(request.BatchSize > 0 ? request.BatchSize : int.MaxValue)
            .ToList();
        var replacedToProcess = latestByItem
            .Where(e => e.NewState == MemoryState.Replaced)
            .OrderBy(e => e.OccurredAt)
            .Take(request.BatchSize > 0 ? request.BatchSize : int.MaxValue)
            .ToList();
        var dormantToProcess = latestByItem
            .Where(e => e.NewState == MemoryState.Dormant)
            .OrderBy(e => e.OccurredAt)
            .Take(request.BatchSize > 0 ? request.BatchSize : int.MaxValue)
            .ToList();

        var extractedCount = supersededToProcess.Count + replacedToProcess.Count + dormantToProcess.Count;
        var processedItemIds = new List<string>();
        var errors = new List<string>();

        // DryRun 模式：仅返回预计处理数量
        if (request.DryRun)
        {
            var dryRunCompletedAt = Now();
            processedItemIds.AddRange(supersededToProcess.Select(e => e.SourceItemId));
            processedItemIds.AddRange(replacedToProcess.Select(e => e.SourceItemId));
            processedItemIds.AddRange(dormantToProcess.Select(e => e.SourceItemId));

            return new ConsolidationRunResult
            {
                RunId = runId,
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                ExtractedCount = extractedCount,
                TransformedCount = 0,
                LoadedCount = 0,
                SkippedCount = 0,
                StartedAt = startedAt,
                CompletedAt = dryRunCompletedAt,
                DryRun = true,
                ProcessedItemIds = processedItemIds,
                Errors = Array.Empty<string>(),
                TriggeredBy = request.TriggeredBy
            };
        }

        // Transform：对 Superseded 状态 item 推进到 Replaced（写入新事件）
        var transformedCount = 0;
        foreach (var evt in supersededToProcess)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evt.NewState.CanTransitionTo(MemoryState.Replaced))
                {
                    continue;
                }
                var replacedEvent = new MemoryStateEventRecord
                {
                    EventId = "evt-" + Guid.NewGuid().ToString("N"),
                    WorkspaceId = evt.WorkspaceId,
                    CollectionId = evt.CollectionId,
                    SourceItemId = evt.SourceItemId,
                    TargetItemId = evt.TargetItemId,
                    ItemType = evt.ItemType,
                    NewState = MemoryState.Replaced,
                    Reason = "consolidation-etl",
                    ReasonDetail = $"Transformed from Superseded by ETL run {runId}",
                    Reviewer = request.TriggeredBy,
                    OccurredAt = Now(),
                    RelationId = evt.RelationId,
                    ConsolidationRunId = runId
                };
                await _store.AppendEventAsync(replacedEvent, cancellationToken);
                transformedCount++;
                processedItemIds.Add(evt.SourceItemId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Transform failed for item '{evt.SourceItemId}': {ex.Message}");
            }
        }

        // Load：对 Replaced 状态 item 推进到 Archived（写入最终事件）
        var loadedCount = 0;
        foreach (var evt in replacedToProcess)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evt.NewState.CanTransitionTo(MemoryState.Archived))
                {
                    continue;
                }
                var archivedEvent = new MemoryStateEventRecord
                {
                    EventId = "evt-" + Guid.NewGuid().ToString("N"),
                    WorkspaceId = evt.WorkspaceId,
                    CollectionId = evt.CollectionId,
                    SourceItemId = evt.SourceItemId,
                    TargetItemId = evt.TargetItemId,
                    ItemType = evt.ItemType,
                    NewState = MemoryState.Archived,
                    Reason = "consolidation-etl",
                    ReasonDetail = $"Loaded (Archived) by ETL run {runId}",
                    Reviewer = request.TriggeredBy,
                    OccurredAt = Now(),
                    RelationId = evt.RelationId,
                    ConsolidationRunId = runId
                };
                await _store.AppendEventAsync(archivedEvent, cancellationToken);
                loadedCount++;
                if (!processedItemIds.Contains(evt.SourceItemId))
                {
                    processedItemIds.Add(evt.SourceItemId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Load failed for item '{evt.SourceItemId}': {ex.Message}");
            }
        }

        // Load (Dormant)：对 Dormant 状态 item 推进到 Archived（彻底降权场景）
        foreach (var evt in dormantToProcess)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!evt.NewState.CanTransitionTo(MemoryState.Archived))
                {
                    continue;
                }
                var archivedEvent = new MemoryStateEventRecord
                {
                    EventId = "evt-" + Guid.NewGuid().ToString("N"),
                    WorkspaceId = evt.WorkspaceId,
                    CollectionId = evt.CollectionId,
                    SourceItemId = evt.SourceItemId,
                    TargetItemId = evt.TargetItemId,
                    ItemType = evt.ItemType,
                    NewState = MemoryState.Archived,
                    Reason = "consolidation-etl-dormant",
                    ReasonDetail = $"Archived from Dormant by ETL run {runId} (terminal decay)",
                    Reviewer = request.TriggeredBy,
                    OccurredAt = Now(),
                    RelationId = evt.RelationId,
                    ConsolidationRunId = runId
                };
                await _store.AppendEventAsync(archivedEvent, cancellationToken);
                loadedCount++;
                if (!processedItemIds.Contains(evt.SourceItemId))
                {
                    processedItemIds.Add(evt.SourceItemId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Load (Dormant) failed for item '{evt.SourceItemId}': {ex.Message}");
            }
        }

        var completedAt = Now();
        return new ConsolidationRunResult
        {
            RunId = runId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ExtractedCount = extractedCount,
            TransformedCount = transformedCount,
            LoadedCount = loadedCount,
            SkippedCount = 0,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DryRun = false,
            ProcessedItemIds = processedItemIds,
            Errors = errors,
            TriggeredBy = request.TriggeredBy
        };
    }

    private DateTimeOffset Now() => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
}
