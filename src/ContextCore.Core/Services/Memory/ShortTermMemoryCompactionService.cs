using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 执行短期记忆的显式压缩与归档。
/// 该服务只处理 working item 合并、archive 判定与 active/archive 持久化，不参与 retrieval。
/// </summary>
public sealed class ShortTermMemoryCompactionService
{
    private const string WorkingKeyMetadataKey = "workingKey";
    private readonly IShortTermMemoryStore _store;
    private readonly ShortTermMemoryCompactionPolicy _policy;

    public ShortTermMemoryCompactionService(
        IShortTermMemoryStore store,
        ShortTermMemoryCompactionPolicy? policy = null)
    {
        _store = store;
        _policy = policy ?? new ShortTermMemoryCompactionPolicy();
    }

    public async Task<ShortTermMemoryCompactionResult> CompactAsync(
        ShortTermMemoryCompactionRequest request,
        string? trigger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var startedAt = DateTimeOffset.UtcNow;
        var effectiveTrigger = string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger.Trim();
        var allRawEvents = await _store.QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var allWorkingItems = await _store.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var targetRawEvents = FilterBySession(allRawEvents, request.SessionId).ToArray();
        var untouchedRawEvents = ExcludeBySession(allRawEvents, request.SessionId).ToArray();
        var targetWorkingItems = FilterBySession(allWorkingItems, request.SessionId).ToArray();
        var untouchedWorkingItems = ExcludeBySession(allWorkingItems, request.SessionId).ToArray();
        var activeRawEventCountBefore = targetRawEvents.Length;
        var activeWorkingItemCountBefore = targetWorkingItems.Length;

        var activeWorkingItems = targetWorkingItems;
        var duplicateArchive = new List<ShortTermWorkingItem>();
        var mergedWorkingItems = 0;
        var mergedByWorkingKeyGroups = 0;
        var mergedByTitleGroups = 0;
        var evidenceRefsTrimmed = 0;

        if (_policy.EnableCompaction)
        {
            var grouped = BuildGroups(targetWorkingItems);
            var compacted = new List<ShortTermWorkingItem>(grouped.Count);
            foreach (var group in grouped)
            {
                var merged = MergeGroup(group, out var duplicates, out var trimmedRefs);
                compacted.Add(merged);
                evidenceRefsTrimmed += trimmedRefs;
                mergedWorkingItems += duplicates.Count;
                duplicateArchive.AddRange(duplicates.Select(item => MarkArchived(item, "compacted_duplicate", startedAt)));
                if (duplicates.Count > 0)
                {
                    if (group.MatchedByWorkingKey)
                    {
                        mergedByWorkingKeyGroups++;
                    }
                    else
                    {
                        mergedByTitleGroups++;
                    }
                }
            }

            activeWorkingItems = compacted.ToArray();
        }

        var archivedRawEvents = new List<ShortTermRawEvent>();
        var archivedWorkingItems = new List<ShortTermWorkingItem>(duplicateArchive);
        var archivedResolvedWorkingItems = 0;

        if (_policy.EnableArchive)
        {
            var archiveRawCandidates = targetRawEvents
                .Where(item => ShouldArchiveRawEvent(item, startedAt))
                .Select(item => MarkArchived(item, "archive_raw_event", startedAt))
                .ToArray();
            archivedRawEvents.AddRange(archiveRawCandidates);
            targetRawEvents = targetRawEvents
                .Where(item => !archiveRawCandidates.Any(archived => string.Equals(archived.EventId, item.EventId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var archiveWorkingCandidates = activeWorkingItems
                .Where(item => ShouldArchiveWorkingItem(item, startedAt))
                .ToArray();
            archivedResolvedWorkingItems = archiveWorkingCandidates.Count(IsResolvedItem);
            archivedWorkingItems.AddRange(archiveWorkingCandidates.Select(item =>
                MarkArchived(
                    item,
                    IsResolvedItem(item) ? "archive_resolved_working_item" : "archive_working_item",
                    startedAt)));
            activeWorkingItems = activeWorkingItems
                .Where(item => !archiveWorkingCandidates.Any(archived => string.Equals(archived.ItemId, item.ItemId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        var finalRawEvents = untouchedRawEvents
            .Concat(targetRawEvents)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.SequenceId)
            .ToArray();
        var finalWorkingItems = untouchedWorkingItems
            .Concat(activeWorkingItems)
            .OrderBy(item => item.UpdatedAt)
            .ThenBy(item => item.CreatedAt)
            .ToArray();

        await _store.ReplaceRawEventsAsync(
            request.WorkspaceId,
            request.CollectionId,
            finalRawEvents,
            cancellationToken).ConfigureAwait(false);
        await _store.ReplaceWorkingItemsAsync(
            request.WorkspaceId,
            request.CollectionId,
            finalWorkingItems,
            cancellationToken).ConfigureAwait(false);

        if (archivedRawEvents.Count > 0)
        {
            await _store.AppendArchivedRawEventsAsync(
                request.WorkspaceId,
                request.CollectionId,
                archivedRawEvents,
                cancellationToken).ConfigureAwait(false);
        }

        if (archivedWorkingItems.Count > 0)
        {
            await _store.AppendArchivedWorkingItemsAsync(
                request.WorkspaceId,
                request.CollectionId,
                archivedWorkingItems,
                cancellationToken).ConfigureAwait(false);
        }

        var archiveSummary = await _store.GetArchiveSummaryAsync(new ShortTermArchiveSummaryQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId
        }, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        if (evidenceRefsTrimmed > 0)
        {
            warnings.Add($"evidence refs 已按上限裁剪 {evidenceRefsTrimmed} 条。");
        }

        if (!_policy.EnableArchive)
        {
            warnings.Add("archive 当前被禁用，本次只执行 compaction。");
        }

        var completedAt = DateTimeOffset.UtcNow;
        var run = new ShortTermCompactionRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            Trigger = effectiveTrigger,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (completedAt - startedAt).TotalMilliseconds,
            CompactedRawEvents = activeRawEventCountBefore - FilterBySession(finalRawEvents, request.SessionId).Count(),
            CompactedWorkingItems = activeWorkingItemCountBefore - FilterBySession(finalWorkingItems, request.SessionId).Count(),
            ArchivedRawEvents = archivedRawEvents.Count,
            ArchivedWorkingItems = archivedWorkingItems.Count,
            RemovedDuplicates = mergedWorkingItems,
            Warnings = warnings,
            Errors = Array.Empty<string>()
        };
        await _store.AppendCompactionRunAsync(run, cancellationToken).ConfigureAwait(false);

        return new ShortTermMemoryCompactionResult
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ActiveRawEventCountBefore = activeRawEventCountBefore,
            ActiveRawEventCountAfter = FilterBySession(finalRawEvents, request.SessionId).Count(),
            ActiveWorkingItemCountBefore = activeWorkingItemCountBefore,
            ActiveWorkingItemCountAfter = FilterBySession(finalWorkingItems, request.SessionId).Count(),
            MergedWorkingItems = mergedWorkingItems,
            MergedByWorkingKeyGroups = mergedByWorkingKeyGroups,
            MergedByTitleGroups = mergedByTitleGroups,
            ArchivedRawEventCount = archivedRawEvents.Count,
            ArchivedWorkingItemCount = archivedWorkingItems.Count,
            ArchivedResolvedWorkingItemCount = archivedResolvedWorkingItems,
            EvidenceRefsTrimmed = evidenceRefsTrimmed,
            ArchiveSummary = archiveSummary,
            Run = run
        };
    }

    public Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(
        ShortTermArchiveSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        return _store.GetArchiveSummaryAsync(query, cancellationToken);
    }

    public async Task<ShortTermArchiveItemsResponse> GetArchiveItemsAsync(
        ShortTermArchiveItemsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);

        var rawEvents = await _store.QueryArchivedRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = query.Limit
        }, cancellationToken).ConfigureAwait(false);
        var workingItems = await _store.QueryArchivedWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Kind = query.Kind,
            Take = query.Limit
        }, cancellationToken).ConfigureAwait(false);

        return new ShortTermArchiveItemsResponse
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Kind = query.Kind,
            RawEvents = rawEvents,
            WorkingItems = workingItems
        };
    }

    public Task<IReadOnlyList<ShortTermCompactionRun>> GetCompactionRunsAsync(
        ShortTermCompactionRunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _store.QueryCompactionRunsAsync(query, cancellationToken);
    }

    public Task<ShortTermCompactionRun?> GetCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _store.GetCompactionRunAsync(runId, cancellationToken);
    }

    private List<WorkingGroup> BuildGroups(IReadOnlyList<ShortTermWorkingItem> items)
    {
        var groups = new List<WorkingGroup>();
        var byWorkingKey = new Dictionary<string, WorkingGroup>(StringComparer.OrdinalIgnoreCase);
        var byTitleKey = new Dictionary<string, WorkingGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.OrderByDescending(static item => item.UpdatedAt).ThenByDescending(static item => item.CreatedAt))
        {
            var workingKey = ResolveWorkingKey(item);
            var titleKey = BuildTitleKey(item);

            WorkingGroup? group = null;
            if (!string.IsNullOrWhiteSpace(workingKey))
            {
                byWorkingKey.TryGetValue(workingKey, out group);
            }

            if (group is null)
            {
                byTitleKey.TryGetValue(titleKey, out group);
            }

            if (group is null)
            {
                group = new WorkingGroup
                {
                    TitleKey = titleKey
                };
                groups.Add(group);
            }

            if (!string.IsNullOrWhiteSpace(workingKey))
            {
                byWorkingKey[workingKey] = group;
                group.WorkingKeys.Add(workingKey);
            }

            byTitleKey[titleKey] = group;
            group.Items.Add(item);
        }

        foreach (var group in groups)
        {
            group.MatchedByWorkingKey = group.Items.Count > 1 && group.WorkingKeys.Count > 0;
        }

        return groups;
    }

    private ShortTermWorkingItem MergeGroup(
        WorkingGroup group,
        out IReadOnlyList<ShortTermWorkingItem> duplicates,
        out int trimmedRefs)
    {
        var ordered = group.Items
            .OrderByDescending(static item => item.UpdatedAt)
            .ThenByDescending(static item => item.CreatedAt)
            .ThenBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var winner = ordered[0];
        duplicates = ordered.Skip(1).ToArray();

        var uniqueRefs = ordered
            .SelectMany(static item => item.Refs)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var limitedRefs = _policy.MaxEvidenceRefsPerWorkingItem > 0
            ? uniqueRefs.Take(_policy.MaxEvidenceRefsPerWorkingItem).ToArray()
            : uniqueRefs;
        trimmedRefs = Math.Max(0, uniqueRefs.Length - limitedRefs.Length);

        var latestMetadata = ordered
            .Reverse()
            .SelectMany(static item => item.Metadata)
            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        return new ShortTermWorkingItem
        {
            ItemId = winner.ItemId,
            WorkspaceId = winner.WorkspaceId,
            CollectionId = winner.CollectionId,
            SessionId = winner.SessionId,
            Kind = ResolveFirstNonEmpty(ordered.Select(static item => item.Kind)),
            Title = ResolveFirstNonEmpty(ordered.Select(static item => item.Title)),
            Summary = ResolveFirstNonEmpty(ordered.Select(static item => item.Summary)),
            Status = ResolveFirstNonEmpty(ordered.Select(static item => item.Status)),
            Lifecycle = ResolveFirstNonEmpty(ordered.Select(static item => item.Lifecycle)),
            Importance = ordered.Max(static item => item.Importance),
            Tags = ordered
                .SelectMany(static item => item.Tags)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Refs = limitedRefs,
            SourceRefs = ordered
                .SelectMany(static item => item.SourceRefs)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CreatedAt = ordered.Min(static item => item.CreatedAt),
            UpdatedAt = ordered.Max(static item => item.UpdatedAt),
            ExpiresAt = ordered
                .Where(static item => item.ExpiresAt is not null)
                .MaxBy(static item => item.ExpiresAt)?.ExpiresAt,
            Metadata = latestMetadata
        };
    }

    private bool ShouldArchiveRawEvent(ShortTermRawEvent item, DateTimeOffset now)
    {
        return _policy.ArchiveRawEventsAfter > TimeSpan.Zero
            && item.CreatedAt <= now - _policy.ArchiveRawEventsAfter;
    }

    private bool ShouldArchiveWorkingItem(ShortTermWorkingItem item, DateTimeOffset now)
    {
        if (IsResolvedItem(item)
            && _policy.ArchiveResolvedItemsAfter > TimeSpan.Zero
            && item.UpdatedAt <= now - _policy.ArchiveResolvedItemsAfter)
        {
            return true;
        }

        return _policy.ArchiveWorkingItemsAfter > TimeSpan.Zero
            && item.UpdatedAt <= now - _policy.ArchiveWorkingItemsAfter;
    }

    private static bool IsResolvedItem(ShortTermWorkingItem item)
    {
        return item.Status.Contains("resolved", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("done", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static ShortTermRawEvent MarkArchived(ShortTermRawEvent item, string reason, DateTimeOffset archivedAt)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["archivedAt"] = archivedAt.ToString("O"),
            ["archiveReason"] = reason
        };

        return new ShortTermRawEvent
        {
            EventId = item.EventId,
            OperationId = item.OperationId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Source = item.Source,
            EventKind = item.EventKind,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            CreatedAt = item.CreatedAt,
            SequenceId = item.SequenceId,
            Tags = item.Tags.ToArray(),
            Metadata = metadata
        };
    }

    private static ShortTermWorkingItem MarkArchived(ShortTermWorkingItem item, string reason, DateTimeOffset archivedAt)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["archivedAt"] = archivedAt.ToString("O"),
            ["archiveReason"] = reason
        };

        return new ShortTermWorkingItem
        {
            ItemId = item.ItemId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            Status = item.Status,
            Lifecycle = item.Lifecycle,
            Importance = item.Importance,
            Tags = item.Tags.ToArray(),
            Refs = item.Refs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            ExpiresAt = item.ExpiresAt,
            Metadata = metadata
        };
    }

    private static IEnumerable<ShortTermRawEvent> FilterBySession(IEnumerable<ShortTermRawEvent> items, string? sessionId)
    {
        return items.Where(item => SessionMatches(item.SessionId, sessionId));
    }

    private static IEnumerable<ShortTermRawEvent> ExcludeBySession(IEnumerable<ShortTermRawEvent> items, string? sessionId)
    {
        return items.Where(item => !SessionMatches(item.SessionId, sessionId));
    }

    private static IEnumerable<ShortTermWorkingItem> FilterBySession(IEnumerable<ShortTermWorkingItem> items, string? sessionId)
    {
        return items.Where(item => SessionMatches(item.SessionId, sessionId));
    }

    private static IEnumerable<ShortTermWorkingItem> ExcludeBySession(IEnumerable<ShortTermWorkingItem> items, string? sessionId)
    {
        return items.Where(item => !SessionMatches(item.SessionId, sessionId));
    }

    private static bool SessionMatches(string? itemSessionId, string? requestedSessionId)
    {
        return string.IsNullOrWhiteSpace(requestedSessionId)
            || string.Equals(itemSessionId, requestedSessionId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveWorkingKey(ShortTermWorkingItem item)
    {
        return item.Metadata.TryGetValue(WorkingKeyMetadataKey, out var workingKey) && !string.IsNullOrWhiteSpace(workingKey)
            ? $"{item.Kind}:{workingKey.Trim()}"
            : null;
    }

    private static string BuildTitleKey(ShortTermWorkingItem item)
    {
        return $"{item.Kind}:{NormalizeTitle(string.IsNullOrWhiteSpace(item.Title) ? item.Summary : item.Title)}";
    }

    private static string ResolveFirstNonEmpty(IEnumerable<string> candidates)
    {
        return candidates.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string NormalizeTitle(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(static ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            .ToArray();
        return new string(chars.Where(static ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private sealed class WorkingGroup
    {
        public List<ShortTermWorkingItem> Items { get; } = [];

        public HashSet<string> WorkingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string TitleKey { get; init; } = string.Empty;

        public bool MatchedByWorkingKey { get; set; }
    }
}
