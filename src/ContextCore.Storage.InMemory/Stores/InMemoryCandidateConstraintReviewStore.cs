using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 CandidateConstraint 审核记录存储。</summary>
public sealed class InMemoryCandidateConstraintReviewStore : ICandidateConstraintReviewStore
{
    private readonly ConcurrentDictionary<string, CandidateConstraintReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendReviewAsync(
        CandidateConstraintReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.ConstraintId, constraintId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CreatedAt)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<CandidateConstraintReviewRecord>>(results);
    }

    private static CandidateConstraintReviewRecord Normalize(CandidateConstraintReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new CandidateConstraintReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            ConstraintId = record.ConstraintId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ActivatedConstraintId = record.ActivatedConstraintId,
            SourceConstraintGapId = record.SourceConstraintGapId,
            SourceSampleId = record.SourceSampleId,
            SourceOperationId = record.SourceOperationId,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = record.Warnings.ToArray(),
            Errors = record.Errors.ToArray()
        };
    }

    private static CandidateConstraintReviewRecord Clone(CandidateConstraintReviewRecord record) => Normalize(record);
}
