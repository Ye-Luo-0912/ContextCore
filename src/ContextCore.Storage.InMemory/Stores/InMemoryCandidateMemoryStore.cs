using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 CandidateMemory 治理存储。</summary>
public sealed class InMemoryCandidateMemoryStore : ICandidateMemoryStore
{
    private readonly ConcurrentDictionary<string, CandidateMemoryItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(CandidateMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(item);
        _items[normalized.Id] = normalized;
        return Task.CompletedTask;
    }

    public Task<CandidateMemoryItem?> GetAsync(
        string candidateMemoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateMemoryId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _items.TryGetValue(candidateMemoryId, out var item)
                ? Clone(item)
                : null);
    }

    public Task<IReadOnlyList<CandidateMemoryItem>> QueryAsync(
        CandidateMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _items.Values
            .Where(item => Matches(item, query))
            .OrderByDescending(item => item.UpdatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<CandidateMemoryItem>>(results);
    }

    public Task<CandidateMemorySummary> GetSummaryAsync(
        CandidateMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var items = _items.Values
            .Where(item => MatchesForSummary(item, query))
            .Select(Clone)
            .ToArray();

        var stale = items
            .Where(item => IsStale(item, now))
            .OrderBy(item => item.ExpiresAt ?? item.UpdatedAt)
            .Take(20)
            .ToArray();
        var recent = items
            .OrderByDescending(item => item.UpdatedAt)
            .Take(20)
            .ToArray();

        return Task.FromResult(new CandidateMemorySummary
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            SessionId = query.SessionId,
            TotalCount = items.Length,
            CountByType = CountBy(items, item => item.Type),
            CountByStatus = CountBy(items, item => item.Status.ToString()),
            CountByLifecycle = CountBy(items, item => item.Lifecycle),
            StaleCandidates = stale,
            RecentCandidates = recent,
            CreatedAt = now
        });
    }

    private static bool Matches(CandidateMemoryItem item, CandidateMemoryQuery query)
    {
        return MatchesForSummary(item, query)
            && (string.IsNullOrWhiteSpace(query.Type) || string.Equals(item.Type, query.Type, StringComparison.OrdinalIgnoreCase))
            && (query.Status is null || item.Status == query.Status.Value)
            && (string.IsNullOrWhiteSpace(query.Lifecycle) || string.Equals(item.Lifecycle, query.Lifecycle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesForSummary(CandidateMemoryItem item, CandidateMemoryQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStale(CandidateMemoryItem item, DateTimeOffset now)
    {
        return item.Status is ContextMemoryStatus.Deprecated or ContextMemoryStatus.Rejected
            || string.Equals(item.Lifecycle, CandidateMemoryLifecycle.Stale, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, CandidateMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
            || (item.ExpiresAt is not null && item.ExpiresAt <= now);
    }

    private static IReadOnlyDictionary<string, int> CountBy(
        IEnumerable<CandidateMemoryItem> items,
        Func<CandidateMemoryItem, string?> selector)
    {
        return items
            .Select(selector)
            .Select(value => string.IsNullOrWhiteSpace(value) ? "(empty)" : value!)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static CandidateMemoryItem Normalize(CandidateMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = item.CreatedAt == default ? now : item.CreatedAt;
        return new CandidateMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Type = item.Type,
            Title = item.Title,
            Summary = item.Summary,
            Content = item.Content,
            Status = item.Status,
            Lifecycle = string.IsNullOrWhiteSpace(item.Lifecycle) ? CandidateMemoryLifecycle.Current : item.Lifecycle,
            Importance = item.Importance,
            Confidence = item.Confidence,
            SourceRefs = item.SourceRefs.ToArray(),
            EvidenceRefs = item.EvidenceRefs.ToArray(),
            PromotionCandidateId = item.PromotionCandidateId,
            StableReviewCandidateId = item.StableReviewCandidateId,
            LearningCaseId = item.LearningCaseId,
            CreatedAt = createdAt,
            UpdatedAt = item.UpdatedAt == default ? createdAt : item.UpdatedAt,
            ExpiresAt = item.ExpiresAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static CandidateMemoryItem Clone(CandidateMemoryItem item) => Normalize(item);
}
