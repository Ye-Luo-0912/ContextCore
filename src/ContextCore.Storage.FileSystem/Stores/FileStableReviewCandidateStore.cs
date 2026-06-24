using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Stable review 候选项存储。</summary>
public sealed class FileStableReviewCandidateStore : IStableReviewCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileStableReviewCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileStableReviewCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(StableReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetStableReviewCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<StableReviewCandidate>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.StableReviewCandidateId, normalized.StableReviewCandidateId, StringComparison.OrdinalIgnoreCase))
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

    public async Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetStableReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var candidates = await _jsonLines.ReadAsync<StableReviewCandidate>(path, cancellationToken).ConfigureAwait(false);
                var match = candidates.FirstOrDefault(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase));
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

    public async Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<StableReviewCandidate>();
            foreach (var scope in ResolveScopes(query.WorkspaceId, query.CollectionId))
            {
                var path = _paths.GetStableReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var candidates = await _jsonLines.ReadAsync<StableReviewCandidate>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(candidates.Where(candidate => Matches(candidate, query)));
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.CreatedAt)
                    .Skip(Math.Max(0, query.Offset))
                    .Take(query.Limit > 0 ? query.Limit : 20)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetStableReviewCandidateReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<StableReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
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

    public async Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<StableReviewRecord>();
            foreach (var scope in EnumerateReviewScopes())
            {
                var path = _paths.GetStableReviewCandidateReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<StableReviewRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase)));
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.CreatedAt)
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
        return EnumerateScopesByPath(_paths.GetStableReviewCandidatesJsonlPath);
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateReviewScopes()
    {
        return EnumerateScopesByPath(_paths.GetStableReviewCandidateReviewsJsonlPath);
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopesByPath(Func<string, string, string> pathSelector)
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
                            .Where(item => File.Exists(pathSelector(item.WorkspaceId, item.CollectionId!)))
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

    private static StableReviewCandidate Normalize(StableReviewCandidate candidate)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = string.IsNullOrWhiteSpace(candidate.StableReviewCandidateId)
                ? Guid.NewGuid().ToString("N")
                : candidate.StableReviewCandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceCandidateId = candidate.SourceCandidateId,
            SourceTargetItemId = candidate.SourceTargetItemId,
            SourceLearningCaseId = candidate.SourceLearningCaseId,
            Kind = candidate.Kind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            SuggestedStableTarget = candidate.SuggestedStableTarget,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            RiskFlags = candidate.RiskFlags.ToArray(),
            ValidationStatus = string.IsNullOrWhiteSpace(candidate.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : candidate.ValidationStatus,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Status = string.IsNullOrWhiteSpace(candidate.Status)
                ? StableReviewCandidateStatuses.Candidate
                : candidate.Status,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StableReviewCandidate Clone(StableReviewCandidate candidate) => Normalize(candidate);

    private static StableReviewRecord Normalize(StableReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new StableReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            StableReviewCandidateId = item.StableReviewCandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            StableTargetItemId = item.StableTargetItemId,
            StableTargetItemKind = item.StableTargetItemKind,
            TargetLayer = item.TargetLayer,
            SourcePromotionCandidateId = item.SourcePromotionCandidateId,
            SourceTargetItemId = item.SourceTargetItemId,
            SourceLearningCaseId = item.SourceLearningCaseId,
            EvidenceRefs = [.. item.EvidenceRefs],
            ValidationStatus = string.IsNullOrWhiteSpace(item.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : item.ValidationStatus,
            RiskFlags = [.. item.RiskFlags],
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    private static StableReviewRecord Clone(StableReviewRecord item) => Normalize(item);
}
