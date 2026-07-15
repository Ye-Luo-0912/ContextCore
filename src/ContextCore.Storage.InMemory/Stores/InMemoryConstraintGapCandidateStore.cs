using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的约束缺口候选项存储。</summary>
public sealed class InMemoryConstraintGapCandidateStore : IConstraintGapCandidateStore
{
    private readonly ConcurrentDictionary<string, ConstraintGapCandidate> _gaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConstraintGapReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task<ConstraintGapCandidate> SaveAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = CandidateRecordNormalizer.Normalize(candidate);
        var duplicate = _gaps.Values.FirstOrDefault(item => HasSameDedupeKey(item, normalized));
        if (duplicate is not null)
        {
            return Task.FromResult(CandidateRecordNormalizer.Clone(duplicate));
        }

        _gaps[normalized.GapId] = normalized;
        return Task.FromResult(CandidateRecordNormalizer.Clone(normalized));
    }

    public Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _gaps.TryGetValue(gapId, out var gap)
                ? CandidateRecordNormalizer.Clone(gap)
                : null);
    }

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _gaps.Values
            .Where(item => Matches(item, query))
            .OrderByDescending(static item => item.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(CandidateRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ConstraintGapCandidate>>(results);
    }

    public Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_gaps.TryGetValue(gapId, out var existing))
        {
            return Task.FromResult<ConstraintGapCandidate?>(null);
        }

        var metadata = new Dictionary<string, string>(existing.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["lastReviewStatus"] = status,
            ["lastReviewedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (!string.IsNullOrWhiteSpace(reviewer))
        {
            metadata["lastReviewer"] = reviewer.Trim();
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            metadata["lastReviewReason"] = reason.Trim();
        }

        var updated = CandidateRecordNormalizer.Normalize(new ConstraintGapCandidate
        {
            GapId = existing.GapId,
            WorkspaceId = existing.WorkspaceId,
            CollectionId = existing.CollectionId,
            SessionId = existing.SessionId,
            Source = existing.Source,
            SourceSampleId = existing.SourceSampleId,
            SourceOperationId = existing.SourceOperationId,
            ExpectedConstraintText = existing.ExpectedConstraintText,
            MatchedConstraintIds = existing.MatchedConstraintIds.ToArray(),
            SuggestedConstraintTitle = existing.SuggestedConstraintTitle,
            SuggestedConstraintScope = existing.SuggestedConstraintScope,
            SuggestedConstraintType = existing.SuggestedConstraintType,
            Severity = existing.Severity,
            Reason = existing.Reason,
            EvidenceRefs = existing.EvidenceRefs.ToArray(),
            Status = status.Trim(),
            CreatedAt = existing.CreatedAt,
            Metadata = metadata
        });
        _gaps[updated.GapId] = updated;
        return Task.FromResult<ConstraintGapCandidate?>(CandidateRecordNormalizer.Clone(updated));
    }

    public Task AppendReviewAsync(
        ConstraintGapReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.GapId, gapId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ConstraintGapReviewRecord>>(results);
    }

    private static bool Matches(ConstraintGapCandidate item, ConstraintGapCandidateQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Source) || string.Equals(item.Source, query.Source, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SourceSampleId) || string.Equals(item.SourceSampleId, query.SourceSampleId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(item.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Severity) || string.Equals(item.Severity, query.Severity, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSameDedupeKey(ConstraintGapCandidate left, ConstraintGapCandidate right)
    {
        return string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CollectionId, right.CollectionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SourceSampleId, right.SourceSampleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(left.ExpectedConstraintText), NormalizeText(right.ExpectedConstraintText), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGapId(
        string workspaceId,
        string collectionId,
        string expectedConstraintText,
        string sourceSampleId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, NormalizeText(expectedConstraintText), sourceSampleId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"constraint-gap-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
