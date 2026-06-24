using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>内存版 vector lifecycle metadata review candidate 存储；仅用于测试和本地预览。</summary>
public sealed class InMemoryVectorLifecycleMetadataReviewCandidateStore : IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly ConcurrentDictionary<string, VectorLifecycleMetadataReviewCandidate> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(candidate);
        _items[normalized.CandidateId] = normalized;
        return Task.CompletedTask;
    }

    public Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _items.TryGetValue(candidateId, out var candidate)
                ? Clone(candidate)
                : null);
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _items.Values
            .Where(candidate => Matches(candidate, query))
            .OrderByDescending(static candidate => candidate.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 50)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>>(results);
    }

    private static bool Matches(
        VectorLifecycleMetadataReviewCandidate candidate,
        VectorLifecycleMetadataReviewCandidateQuery query)
    {
        return string.Equals(candidate.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(candidate.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(candidate.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Layer) || string.Equals(candidate.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.ItemKind) || string.Equals(candidate.ItemKind, query.ItemKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.MustHitItemId) || string.Equals(candidate.MustHitItemId, query.MustHitItemId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SourceEvalSet) || string.Equals(candidate.SourceEvalSet, query.SourceEvalSet, StringComparison.OrdinalIgnoreCase));
    }

    private static VectorLifecycleMetadataReviewCandidate Normalize(VectorLifecycleMetadataReviewCandidate candidate)
    {
        return new VectorLifecycleMetadataReviewCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(candidate.CandidateId) ? Guid.NewGuid().ToString("N") : candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceSampleId = candidate.SourceSampleId,
            SourceEvalSet = candidate.SourceEvalSet,
            MustHitItemId = candidate.MustHitItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            CurrentLifecycle = candidate.CurrentLifecycle,
            CurrentReviewStatus = candidate.CurrentReviewStatus,
            CurrentTargetSection = candidate.CurrentTargetSection,
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = candidate.ProposedTargetSection,
            RepairReason = candidate.RepairReason,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray(),
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = candidate.RiskIfApproved.ToArray(),
            RiskIfRejected = candidate.RiskIfRejected.ToArray(),
            RequiresHumanReview = candidate.RequiresHumanReview,
            Status = string.IsNullOrWhiteSpace(candidate.Status)
                ? VectorLifecycleMetadataReviewCandidateStatuses.PendingReview
                : candidate.Status,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static VectorLifecycleMetadataReviewCandidate Clone(VectorLifecycleMetadataReviewCandidate candidate)
        => Normalize(candidate);
}
