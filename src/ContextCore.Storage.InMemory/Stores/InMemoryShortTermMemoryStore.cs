using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的短期记忆存储，主要用于测试和 memory provider。</summary>
public sealed class InMemoryShortTermMemoryStore : IShortTermMemoryStore
{
    private readonly ShortTermMemoryPolicy _policy;
    private readonly ConcurrentDictionary<string, List<ShortTermRawEvent>> _rawEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ShortTermWorkingItem>> _workingItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ShortTermRawEvent>> _archivedRawEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ShortTermWorkingItem>> _archivedWorkingItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ShortTermCompactionRun>> _compactionRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public InMemoryShortTermMemoryStore(ShortTermMemoryPolicy policy)
    {
        _policy = policy;
    }

    public Task AppendRawEventAsync(ShortTermRawEvent rawEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(rawEvent);
        lock (_gate)
        {
            var key = Key(normalized.WorkspaceId, normalized.CollectionId);
            var items = _rawEvents.GetOrAdd(key, _ => new List<ShortTermRawEvent>());
            items.Add(normalized);
        }

        return Task.CompletedTask;
    }

    public Task SaveWorkingItemAsync(ShortTermWorkingItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(item);
        lock (_gate)
        {
            var key = Key(normalized.WorkspaceId, normalized.CollectionId);
            var items = _workingItems.GetOrAdd(key, _ => new List<ShortTermWorkingItem>());
            items = items.Where(existing => !string.Equals(existing.ItemId, normalized.ItemId, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Add(normalized);
            _workingItems[key] = items.OrderBy(existing => existing.UpdatedAt).ToList();
        }

        return Task.CompletedTask;
    }

    public Task ReplaceRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _rawEvents[Key(workspaceId, collectionId)] = items.Select(Normalize).OrderBy(item => item.CreatedAt).ToList();
        }

        return Task.CompletedTask;
    }

    public Task ReplaceWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _workingItems[Key(workspaceId, collectionId)] = items.Select(Normalize).OrderBy(item => item.UpdatedAt).ToList();
        }

        return Task.CompletedTask;
    }

    public Task AppendArchivedRawEventsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermRawEvent> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = Key(workspaceId, collectionId);
            var existing = _archivedRawEvents.GetOrAdd(key, _ => new List<ShortTermRawEvent>());
            existing.AddRange(items.Select(Normalize));
        }

        return Task.CompletedTask;
    }

    public Task AppendArchivedWorkingItemsAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ShortTermWorkingItem> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = Key(workspaceId, collectionId);
            var existing = _archivedWorkingItems.GetOrAdd(key, _ => new List<ShortTermWorkingItem>());
            existing.AddRange(items.Select(Normalize));
        }

        return Task.CompletedTask;
    }

    public Task<ShortTermWorkingItem?> GetWorkingItemAsync(string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(QueryWorkingItemsInternal(new ShortTermWorkingItemQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, archived: false).FirstOrDefault(item => string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<ShortTermRawEvent>> QueryRawEventsAsync(ShortTermRawEventQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ShortTermRawEvent>>(QueryRawEventsInternal(query, archived: false).ToArray());
        }
    }

    public Task<IReadOnlyList<ShortTermWorkingItem>> QueryWorkingItemsAsync(ShortTermWorkingItemQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ShortTermWorkingItem>>(QueryWorkingItemsInternal(query, archived: false).ToArray());
        }
    }

    public Task<IReadOnlyList<ShortTermRawEvent>> QueryArchivedRawEventsAsync(
        ShortTermRawEventQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ShortTermRawEvent>>(QueryRawEventsInternal(query, archived: true).ToArray());
        }
    }

    public Task<IReadOnlyList<ShortTermWorkingItem>> QueryArchivedWorkingItemsAsync(
        ShortTermWorkingItemQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ShortTermWorkingItem>>(QueryWorkingItemsInternal(query, archived: true).ToArray());
        }
    }

    public Task<IReadOnlyList<ShortTermMemoryScope>> QueryScopesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var scopes = _rawEvents.Keys
                .Concat(_workingItems.Keys)
                .Concat(_archivedRawEvents.Keys)
                .Concat(_archivedWorkingItems.Keys)
                .Concat(_compactionRuns.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ParseScope)
                .ToArray();
            return Task.FromResult<IReadOnlyList<ShortTermMemoryScope>>(scopes);
        }
    }

    public async Task<ShortTermMemorySummary> GetSummaryAsync(ShortTermSummaryQuery query, CancellationToken cancellationToken = default)
    {
        var raw = await QueryRawEventsAsync(new ShortTermRawEventQuery
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

        var activeTasks = working.Where(item => IsWorkingKind(item, "ActiveTask", "task")).Take(10).ToArray();
        var recentDecisions = working.Where(item => IsWorkingKind(item, "RecentDecision", "decision")).Take(10).ToArray();
        var openQuestions = working.Where(item => IsWorkingKind(item, "OpenQuestion", "question")).Take(10).ToArray();
        var knownIssues = working.Where(item => IsWorkingKind(item, "KnownIssue", "issue")).Take(10).ToArray();
        var recentWarnings = working.Where(item => IsWorkingKind(item, "RecentWarning", "warning")).Take(10).ToArray();

        return new ShortTermMemorySummary
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            RawEventCount = raw.Count,
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
            LatestRawEvents = raw.OrderByDescending(item => item.CreatedAt).Take(query.LatestRawTake > 0 ? query.LatestRawTake : 10).ToArray(),
            Policy = _policy
        };
    }

    public Task<ShortTermArchiveSummary> GetArchiveSummaryAsync(ShortTermArchiveSummaryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var raw = QueryArchivedRawEventsInternal(query).ToArray();
            var working = QueryArchivedWorkingItemsInternal(query).ToArray();
            return Task.FromResult(new ShortTermArchiveSummary
            {
                WorkspaceId = query.WorkspaceId,
                CollectionId = query.CollectionId,
                SessionId = query.SessionId,
                ArchivedRawEventCount = raw.Length,
                ArchivedWorkingItemCount = working.Length,
                ArchivedResolvedWorkingItemCount = working.Count(IsResolvedItem),
                ArchivedActiveTaskCount = working.Count(item => IsWorkingKind(item, "ActiveTask", "task")),
                ArchivedRecentDecisionCount = working.Count(item => IsWorkingKind(item, "RecentDecision", "decision")),
                ArchivedOpenQuestionCount = working.Count(item => IsWorkingKind(item, "OpenQuestion", "question")),
                ArchivedKnownIssueCount = working.Count(item => IsWorkingKind(item, "KnownIssue", "issue")),
                ArchivedRecentWarningCount = working.Count(item => IsWorkingKind(item, "RecentWarning", "warning")),
                LatestArchivedAt = raw
                    .Select(item => item.Metadata.GetValueOrDefault("archivedAt"))
                    .Concat(working.Select(item => item.Metadata.GetValueOrDefault("archivedAt")))
                    .Select(ParseDateTimeOffset)
                    .Where(value => value is not null)
                    .Max()
            });
        }
    }

    public Task AppendCompactionRunAsync(ShortTermCompactionRun run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(run);
        lock (_gate)
        {
            var key = Key(normalized.WorkspaceId, normalized.CollectionId);
            var runs = _compactionRuns.GetOrAdd(key, _ => new List<ShortTermCompactionRun>());
            runs.Add(normalized);
            _compactionRuns[key] = runs.OrderBy(item => item.StartedAt).ToList();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShortTermCompactionRun>> QueryCompactionRunsAsync(
        ShortTermCompactionRunQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var results = _compactionRuns
                .Where(pair => ScopeMatches(pair.Key, query.WorkspaceId, query.CollectionId))
                .SelectMany(pair => pair.Value)
                .Where(item => string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(query.Trigger) || string.Equals(item.Trigger, query.Trigger, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.StartedAt)
                .Take(query.Take > 0 ? query.Take : 20)
                .Select(Clone)
                .ToArray();
            return Task.FromResult<IReadOnlyList<ShortTermCompactionRun>>(results);
        }
    }

    public Task<ShortTermCompactionRun?> GetCompactionRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = _compactionRuns.Values
                .SelectMany(static value => value)
                .FirstOrDefault(item => string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(run is null ? null : Clone(run));
        }
    }

    private IEnumerable<ShortTermRawEvent> QueryRawEventsInternal(ShortTermRawEventQuery query, bool archived)
    {
        var source = archived ? _archivedRawEvents : _rawEvents;
        return source
            .Where(pair => ScopeMatches(pair.Key, query.WorkspaceId, query.CollectionId))
            .SelectMany(pair => pair.Value)
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.Source) || string.Equals(item.Source, query.Source, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.EventKind) || string.Equals(item.EventKind, query.EventKind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Take(query.Take > 0 ? query.Take : 100)
            .Select(Clone);
    }

    private IEnumerable<ShortTermWorkingItem> QueryWorkingItemsInternal(ShortTermWorkingItemQuery query, bool archived)
    {
        var source = archived ? _archivedWorkingItems : _workingItems;
        return source
            .Where(pair => ScopeMatches(pair.Key, query.WorkspaceId, query.CollectionId))
            .SelectMany(pair => pair.Value)
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.Kind) || string.Equals(item.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.Status) || string.Equals(item.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(query.Take > 0 ? query.Take : 100)
            .Select(Clone);
    }

    private IEnumerable<ShortTermRawEvent> QueryArchivedRawEventsInternal(ShortTermArchiveSummaryQuery query)
    {
        return QueryRawEventsInternal(new ShortTermRawEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, archived: true);
    }

    private IEnumerable<ShortTermWorkingItem> QueryArchivedWorkingItemsInternal(ShortTermArchiveSummaryQuery query)
    {
        return QueryWorkingItemsInternal(new ShortTermWorkingItemQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            Take = int.MaxValue
        }, archived: true);
    }

    private static bool ScopeMatches(string key, string? workspaceId, string? collectionId)
    {
        var scope = ParseScope(key);
        return (string.IsNullOrWhiteSpace(workspaceId) || string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(collectionId) || string.Equals(scope.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
    }

    private static ShortTermMemoryScope ParseScope(string key)
    {
        var parts = key.Split('\u001f', 2);
        return new ShortTermMemoryScope
        {
            WorkspaceId = parts[0],
            CollectionId = parts.Length > 1 ? parts[1] : string.Empty
        };
    }

    private static string Key(string workspaceId, string? collectionId)
        => collectionId is null ? workspaceId : $"{workspaceId}\u001f{collectionId}";

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
            Warnings = item.Warnings.ToArray(),
            Errors = item.Errors.ToArray()
        };
    }

    private static ShortTermRawEvent Clone(ShortTermRawEvent item) => Normalize(item);

    private static ShortTermWorkingItem Clone(ShortTermWorkingItem item) => Normalize(item);

    private static ShortTermCompactionRun Clone(ShortTermCompactionRun item) => Normalize(item);

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
