using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 vector lifecycle metadata review candidate 存储；仅保存人工 review 队列。</summary>
public sealed class FileVectorLifecycleMetadataReviewCandidateStore : IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileVectorLifecycleMetadataReviewCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleMetadataReviewCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewCandidate>(path, cancellationToken)
                .ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.CandidateId, normalized.CandidateId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var candidates = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewCandidate>(path, cancellationToken)
                    .ConfigureAwait(false);
                var match = candidates.FirstOrDefault(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return Clone(match);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<VectorLifecycleMetadataReviewCandidate>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId))
            {
                var path = _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var candidates = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewCandidate>(path, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(candidates.Where(candidate => Matches(candidate, query)));
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.CreatedAt)
                    .Skip(Math.Max(0, query.Offset))
                    .Take(query.Limit > 0 ? query.Limit : 50)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return
        [
            .. EnumerateScopes()
                .Where(scope => string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        ];
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopes()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<ShortTermMemoryScope>();
        }

        return
        [
            .. Directory.EnumerateDirectories(workspacesRoot)
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

                    return
                    [
                        .. Directory.EnumerateDirectories(collectionsRoot)
                            .Select(collectionDirectory => new
                            {
                                WorkspaceId = workspaceId,
                                CollectionId = Path.GetFileName(collectionDirectory)
                            })
                            .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                            .Where(item => File.Exists(_paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(item.WorkspaceId, item.CollectionId!)))
                            .Select(item => new ShortTermMemoryScope
                            {
                                WorkspaceId = item.WorkspaceId,
                                CollectionId = item.CollectionId
                            })
                    ];
                })
                .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
        ];
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
            EvidenceRefs = [.. candidate.EvidenceRefs],
            SourceRefs = [.. candidate.SourceRefs],
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = [.. candidate.RiskIfApproved],
            RiskIfRejected = [.. candidate.RiskIfRejected],
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
