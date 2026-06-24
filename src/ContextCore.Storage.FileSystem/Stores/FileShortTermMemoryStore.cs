using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的短期记忆存储。
/// active 与 archive 分路径保存，archive 为保留而非删除。
/// </summary>
public sealed class FileShortTermMemoryStore : IShortTermMemoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly ShortTermMemoryPolicy _policy;

    public FileShortTermMemoryStore(FilePathResolver paths, FileFormatSerializer serializer, ShortTermMemoryPolicy policy)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _policy = policy;
    }

    public async Task AppendRawEventAsync(ShortTermRawEvent rawEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);
        var normalized = Normalize(rawEvent);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermRawEventsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var items = await ReadJsonLinesWithLegacyAsync<ShortTermRawEvent>(
                path,
                _paths.GetLegacyShortTermRawEventsJsonlPath(normalized.WorkspaceId, normalized.CollectionId),
                static item => item.EventId,
                cancellationToken).ConfigureAwait(false);
            await _jsonLines.WriteAsync(path, items.Append(normalized), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveWorkingItemAsync(ShortTermWorkingItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalized = Normalize(item);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermWorkingItemsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var items = await ReadJsonLinesWithLegacyAsync<ShortTermWorkingItem>(
                path,
                _paths.GetLegacyShortTermWorkingItemsJsonlPath(normalized.WorkspaceId, normalized.CollectionId),
                static item => item.ItemId,
                cancellationToken).ConfigureAwait(false);
            var updated = items
                .Where(existing => !string.Equals(existing.ItemId, normalized.ItemId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderBy(itemValue => itemValue.UpdatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermRawEventsJsonlPath(workspaceId, collectionId);
            await _jsonLines.WriteAsync(path, items.Select(Normalize), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermWorkingItemsJsonlPath(workspaceId, collectionId);
            await _jsonLines.WriteAsync(path, items.Select(Normalize), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendArchivedRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermArchivedRawEventsJsonlPath(workspaceId, collectionId);
            var existing = await ReadJsonLinesWithLegacyAsync<ShortTermRawEvent>(
                path,
                _paths.GetLegacyShortTermArchivedRawEventsJsonlPath(workspaceId, collectionId),
                static item => item.EventId,
                cancellationToken).ConfigureAwait(false);
            await _jsonLines.WriteAsync(path, existing.Concat(items.Select(Normalize)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendArchivedWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermArchivedWorkingItemsJsonlPath(workspaceId, collectionId);
            var existing = await ReadJsonLinesWithLegacyAsync<ShortTermWorkingItem>(
                path,
                _paths.GetLegacyShortTermArchivedWorkingItemsJsonlPath(workspaceId, collectionId),
                static item => item.ItemId,
                cancellationToken).ConfigureAwait(false);
            await _jsonLines.WriteAsync(path, existing.Concat(items.Select(Normalize)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShortTermWorkingItem?> GetWorkingItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var items = await QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item => string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsAsync(ShortTermRawEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryRawEventsUnlockedAsync(query, archived: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsAsync(ShortTermWorkingItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryWorkingItemsUnlockedAsync(query, archived: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortTermRawEvent>> QueryArchivedRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryRawEventsUnlockedAsync(query, archived: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> QueryArchivedWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryWorkingItemsUnlockedAsync(query, archived: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortTermMemoryScope>> QueryScopesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return EnumerateScopes();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShortTermMemorySummary> GetSummaryAsync(ShortTermSummaryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rawEvents = await QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = query.LatestRawTake
        }, cancellationToken).ConfigureAwait(false);
        var allRaw = await QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var working = await QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return BuildSummary(query.WorkspaceId, query.CollectionId, query.SessionId, allRaw, rawEvents, working);
    }

    public async Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(
        ShortTermArchiveSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rawEvents = await QueryArchivedRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var working = await QueryArchivedWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return BuildArchiveSummary(query.WorkspaceId, query.CollectionId, query.SessionId, rawEvents, working);
    }

    public async Task AppendCompactionRunAsync(ShortTermCompactionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermCompactionRunsJsonlPath(run.WorkspaceId, run.CollectionId);
            var existing = await ReadJsonLinesWithLegacyAsync<ShortTermCompactionRun>(
                path,
                _paths.GetLegacyShortTermCompactionRunsJsonlPath(run.WorkspaceId, run.CollectionId),
                static item => item.RunId,
                cancellationToken).ConfigureAwait(false);
            await _jsonLines.WriteAsync(path, existing.Append(Normalize(run)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortTermCompactionRun>> QueryCompactionRunsAsync(
        ShortTermCompactionRunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopes = ResolveScopes(query.WorkspaceId, query.CollectionId);
            var results = new List<ShortTermCompactionRun>();
            foreach (var scope in scopes)
            {
                var path = _paths.GetShortTermCompactionRunsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await ReadJsonLinesWithLegacyAsync<ShortTermCompactionRun>(
                    path,
                    _paths.GetLegacyShortTermCompactionRunsJsonlPath(scope.WorkspaceId, scope.CollectionId),
                    static item => item.RunId,
                    cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => Matches(item, query)));
            }

            return results
                .OrderByDescending(item => item.StartedAt)
                .Take(query.Take > 0 ? query.Take : 20)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShortTermCompactionRun?> GetCompactionRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetShortTermCompactionRunsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await ReadJsonLinesWithLegacyAsync<ShortTermCompactionRun>(
                    path,
                    _paths.GetLegacyShortTermCompactionRunsJsonlPath(scope.WorkspaceId, scope.CollectionId),
                    static item => item.RunId,
                    cancellationToken).ConfigureAwait(false);
                var run = items.FirstOrDefault(item => string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase));
                if (run is not null)
                {
                    return run;
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsUnlockedAsync(
        ShortTermRawEventQuery query,
        bool archived,
        CancellationToken cancellationToken)
    {
        var scopes = ResolveScopes(query.WorkspaceId, query.CollectionId);
        var results = new List<ShortTermRawEvent>();
        foreach (var scope in scopes)
        {
            var path = archived
                ? _paths.GetShortTermArchivedRawEventsJsonlPath(scope.WorkspaceId, scope.CollectionId)
                : _paths.GetShortTermRawEventsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var legacyPath = archived
                ? _paths.GetLegacyShortTermArchivedRawEventsJsonlPath(scope.WorkspaceId, scope.CollectionId)
                : _paths.GetLegacyShortTermRawEventsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await ReadJsonLinesWithLegacyAsync<ShortTermRawEvent>(
                path,
                legacyPath,
                static item => item.EventId,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => Matches(item, query)));
        }

        return results
            .OrderByDescending(item => item.CreatedAt)
            .Take(query.Take > 0 ? query.Take : 100)
            .ToArray();
    }

    private async Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsUnlockedAsync(
        ShortTermWorkingItemQuery query,
        bool archived,
        CancellationToken cancellationToken)
    {
        var scopes = ResolveScopes(query.WorkspaceId, query.CollectionId);
        var results = new List<ShortTermWorkingItem>();
        foreach (var scope in scopes)
        {
            var path = archived
                ? _paths.GetShortTermArchivedWorkingItemsJsonlPath(scope.WorkspaceId, scope.CollectionId)
                : _paths.GetShortTermWorkingItemsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var legacyPath = archived
                ? _paths.GetLegacyShortTermArchivedWorkingItemsJsonlPath(scope.WorkspaceId, scope.CollectionId)
                : _paths.GetLegacyShortTermWorkingItemsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await ReadJsonLinesWithLegacyAsync<ShortTermWorkingItem>(
                path,
                legacyPath,
                static item => item.ItemId,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => Matches(item, query)));
        }

        return results
            .OrderByDescending(item => item.UpdatedAt)
            .Take(query.Take > 0 ? query.Take : 100)
            .ToArray();
    }

    private ShortTermMemorySummary BuildSummary(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        IReadOnlyList<ShortTermRawEvent> allRaw,
        IReadOnlyList<ShortTermRawEvent> latestRaw,
        IReadOnlyList<ShortTermWorkingItem> working)
    {
        var activeTasks = working.Where(item => IsWorkingKind(item, "ActiveTask", "task")).Take(10).ToArray();
        var recentDecisions = working.Where(item => IsWorkingKind(item, "RecentDecision", "decision")).Take(10).ToArray();
        var openQuestions = working.Where(item => IsWorkingKind(item, "OpenQuestion", "question")).Take(10).ToArray();
        var knownIssues = working.Where(item => IsWorkingKind(item, "KnownIssue", "issue")).Take(10).ToArray();
        var recentWarnings = working.Where(item => IsWorkingKind(item, "RecentWarning", "warning")).Take(10).ToArray();

        return new ShortTermMemorySummary
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            RawEventCount = allRaw.Count,
            WorkingItemCount = working.Count,
            ActiveTaskCount = working.Count(item => IsWorkingKind(item, "ActiveTask", "task")),
            RecentDecisionCount = working.Count(item => IsWorkingKind(item, "RecentDecision", "decision")),
            OpenQuestionCount = working.Count(item => IsWorkingKind(item, "OpenQuestion", "question")),
            KnownIssueCount = working.Count(item => IsWorkingKind(item, "KnownIssue", "issue")),
            RecentWarningCount = working.Count(item => IsWorkingKind(item, "RecentWarning", "warning")),
            ActiveTasks = activeTasks,
            RecentDecisions = recentDecisions,
            OpenQuestions = openQuestions,
            KnownIssues = knownIssues,
            RecentWarnings = recentWarnings,
            LatestRawEvents = latestRaw,
            Policy = _policy
        };
    }

    private static ShortTermArchiveSummary BuildArchiveSummary(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        IReadOnlyList<ShortTermRawEvent> rawEvents,
        IReadOnlyList<ShortTermWorkingItem> workingItems)
    {
        return new ShortTermArchiveSummary
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            ArchivedRawEventCount = rawEvents.Count,
            ArchivedWorkingItemCount = workingItems.Count,
            ArchivedResolvedWorkingItemCount = workingItems.Count(IsResolvedItem),
            ArchivedActiveTaskCount = workingItems.Count(item => IsWorkingKind(item, "ActiveTask", "task")),
            ArchivedRecentDecisionCount = workingItems.Count(item => IsWorkingKind(item, "RecentDecision", "decision")),
            ArchivedOpenQuestionCount = workingItems.Count(item => IsWorkingKind(item, "OpenQuestion", "question")),
            ArchivedKnownIssueCount = workingItems.Count(item => IsWorkingKind(item, "KnownIssue", "issue")),
            ArchivedRecentWarningCount = workingItems.Count(item => IsWorkingKind(item, "RecentWarning", "warning")),
            LatestArchivedAt = rawEvents
                .Select(item => item.Metadata.GetValueOrDefault("archivedAt"))
                .Concat(workingItems.Select(item => item.Metadata.GetValueOrDefault("archivedAt")))
                .Select(ParseDateTimeOffset)
                .Where(static value => value is not null)
                .Max()
        };
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopes()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<ShortTermMemoryScope>();
        }

        return Directory.EnumerateDirectories(workspacesRoot)
            .SelectMany(workspaceDirectory =>
            {
                var workspaceId = Path.GetFileName(workspaceDirectory);
                if (string.IsNullOrWhiteSpace(workspaceId))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                if (!Directory.Exists(collectionsRoot))
                {
                    return Array.Empty<ShortTermMemoryScope>();
                }

                return Directory.EnumerateDirectories(collectionsRoot)
                    .Select(collectionDirectory => new
                    {
                        WorkspaceId = workspaceId,
                        CollectionId = Path.GetFileName(collectionDirectory)
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                    .Where(item => HasShortTermData(item.WorkspaceId, item.CollectionId))
                    .Select(item => new ShortTermMemoryScope
                    {
                        WorkspaceId = item.WorkspaceId,
                        CollectionId = item.CollectionId
                    })
                    .ToArray();
            })
            .DistinctBy(static item => $"{item.WorkspaceId}\u001f{item.CollectionId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string? workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        var scopes = EnumerateScopes();
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return scopes;
        }

        return
        [
            .. scopes
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(collectionId) || string.Equals(item.CollectionId, collectionId,
                    StringComparison.OrdinalIgnoreCase))
        ];
    }

    private bool HasShortTermData(string workspaceId, string collectionId)
    {
        return Directory.Exists(_paths.GetShortTermDirectory(workspaceId, collectionId))
            || Directory.Exists(_paths.GetLegacyShortTermDirectory(workspaceId, collectionId))
            || File.Exists(_paths.GetShortTermCompactionRunsJsonlPath(workspaceId, collectionId))
            || File.Exists(_paths.GetLegacyShortTermCompactionRunsJsonlPath(workspaceId, collectionId));
    }

    private async Task<IReadOnlyList<T>> ReadJsonLinesWithLegacyAsync<T>(
        string primaryPath,
        string legacyPath,
        Func<T, string?> keySelector,
        CancellationToken cancellationToken)
    {
        var primary = await _jsonLines.ReadAsync<T>(primaryPath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(primaryPath, legacyPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacyPath))
        {
            return primary;
        }

        var legacy = await _jsonLines.ReadAsync<T>(legacyPath, cancellationToken).ConfigureAwait(false);
        if (legacy.Count == 0)
        {
            return primary;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<T>(primary.Count + legacy.Count);
        foreach (var item in primary)
        {
            merged.Add(item);
            var key = keySelector(item);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }

        foreach (var item in legacy)
        {
            var key = keySelector(item);
            if (string.IsNullOrWhiteSpace(key) || keys.Add(key))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static bool Matches(ShortTermRawEvent item, ShortTermRawEventQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Source) || string.Equals(item.Source, query.Source, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.EventKind) || string.Equals(item.EventKind, query.EventKind, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(ShortTermWorkingItem item, ShortTermWorkingItemQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Kind) || string.Equals(item.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(item.Status, query.Status, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(ShortTermCompactionRun item, ShortTermCompactionRunQuery query)
    {
        return (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Trigger) || string.Equals(item.Trigger, query.Trigger, StringComparison.OrdinalIgnoreCase));
    }

    private static ShortTermRawEvent Normalize(ShortTermRawEvent item)
    {
        return new ShortTermRawEvent
        {
            EventId = string.IsNullOrWhiteSpace(item.EventId) ? Guid.NewGuid().ToString("N") : item.EventId,
            OperationId = item.OperationId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Source = item.Source,
            EventKind = item.EventKind,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            SequenceId = item.SequenceId,
            Tags = item.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermWorkingItem Normalize(ShortTermWorkingItem item)
    {
        var now = DateTimeOffset.UtcNow;
        return new ShortTermWorkingItem
        {
            ItemId = string.IsNullOrWhiteSpace(item.ItemId) ? Guid.NewGuid().ToString("N") : item.ItemId,
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
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt,
            ExpiresAt = item.ExpiresAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermCompactionRun Normalize(ShortTermCompactionRun item)
    {
        return new ShortTermCompactionRun
        {
            RunId = string.IsNullOrWhiteSpace(item.RunId) ? Guid.NewGuid().ToString("N") : item.RunId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Trigger = item.Trigger,
            StartedAt = item.StartedAt == default ? DateTimeOffset.UtcNow : item.StartedAt,
            CompletedAt = item.CompletedAt == default ? DateTimeOffset.UtcNow : item.CompletedAt,
            DurationMs = item.DurationMs,
            CompactedRawEvents = item.CompactedRawEvents,
            CompactedWorkingItems = item.CompactedWorkingItems,
            ArchivedRawEvents = item.ArchivedRawEvents,
            ArchivedWorkingItems = item.ArchivedWorkingItems,
            RemovedDuplicates = item.RemovedDuplicates,
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    private static bool IsWorkingKind(ShortTermWorkingItem item, string canonicalKind, string legacyToken)
    {
        return string.Equals(item.Kind, canonicalKind, StringComparison.OrdinalIgnoreCase)
            || item.Kind.Contains(legacyToken, StringComparison.OrdinalIgnoreCase)
            || item.Tags.Contains(canonicalKind, StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains(legacyToken, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsResolvedItem(ShortTermWorkingItem item)
    {
        return item.Status.Contains("resolved", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("done", StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
