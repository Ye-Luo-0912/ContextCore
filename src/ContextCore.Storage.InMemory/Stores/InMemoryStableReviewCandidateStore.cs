using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 Stable review 候选项存储。</summary>
public sealed class InMemoryStableReviewCandidateStore : IStableReviewCandidateStore
{
    private readonly ConcurrentDictionary<string, StableReviewCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StableReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(StableReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = CandidateRecordNormalizer.Normalize(candidate);
        _candidates[normalized.StableReviewCandidateId] = normalized;
        return Task.CompletedTask;
    }

    public Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _candidates.TryGetValue(stableReviewCandidateId, out var candidate)
                ? CandidateRecordNormalizer.Clone(candidate)
                : null);
    }

    public Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _candidates.Values
            .Where(candidate => Matches(candidate, query))
            .OrderByDescending(static candidate => candidate.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(CandidateRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StableReviewCandidate>>(results);
    }

    public Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StableReviewRecord>>(results);
    }

    private static bool Matches(StableReviewCandidate candidate, StableReviewCandidateQuery query)
    {
        return string.Equals(candidate.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(candidate.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(candidate.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(candidate.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.ValidationStatus) || string.Equals(candidate.ValidationStatus, query.ValidationStatus, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Kind) || string.Equals(candidate.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SuggestedStableTarget) || string.Equals(candidate.SuggestedStableTarget, query.SuggestedStableTarget, StringComparison.OrdinalIgnoreCase));
    }
}
