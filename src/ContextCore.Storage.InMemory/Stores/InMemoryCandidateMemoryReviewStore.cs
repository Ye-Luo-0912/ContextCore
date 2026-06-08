using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 CandidateMemory review / cleanup 审核历史存储。</summary>
public sealed class InMemoryCandidateMemoryReviewStore : ICandidateMemoryReviewStore
{
    private readonly ConcurrentDictionary<string, CandidateMemoryReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendReviewAsync(
        CandidateMemoryReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Select(Clone)
            .ToArray();
        return Task.FromResult<IReadOnlyList<CandidateMemoryReviewRecord>>(results);
    }

    private static CandidateMemoryReviewRecord Normalize(CandidateMemoryReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new CandidateMemoryReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            CandidateId = record.CandidateId,
            CandidateKind = record.CandidateKind,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            SupersedeTargetCandidateId = record.SupersedeTargetCandidateId,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            SourceRefs = record.SourceRefs.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = record.Warnings.ToArray(),
            Errors = record.Errors.ToArray()
        };
    }

    private static CandidateMemoryReviewRecord Clone(CandidateMemoryReviewRecord record) => Normalize(record);
}
