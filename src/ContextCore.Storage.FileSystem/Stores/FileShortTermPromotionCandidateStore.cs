using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的短期晋升候选项存储。</summary>
public sealed class FileShortTermPromotionCandidateStore : IShortTermPromotionCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileShortTermPromotionCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermPromotionCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<ShortTermPromotionCandidate>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.CandidateId, normalized.CandidateId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShortTermPromotionCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetShortTermPromotionCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<ShortTermPromotionCandidate>(path, cancellationToken).ConfigureAwait(false);
                var match = items.FirstOrDefault(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
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

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scopes = ResolveScopes(query.WorkspaceId, query.CollectionId);
            var results = new List<ShortTermPromotionCandidate>();
            foreach (var scope in scopes)
            {
                var path = _paths.GetShortTermPromotionCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<ShortTermPromotionCandidate>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => Matches(item, query)));
            }

            return results
                .Where(item => string.IsNullOrWhiteSpace(query.Kind) || string.Equals(item.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(query.SuggestedTargetLayer) || string.Equals(item.SuggestedTargetLayer, query.SuggestedTargetLayer, StringComparison.OrdinalIgnoreCase))
                .Where(item => query.MinConfidence is null || item.Confidence >= query.MinConfidence.Value)
                .Where(item => query.MinImportance is null || item.Importance >= query.MinImportance.Value)
                .OrderByDescending(item => item.CreatedAt)
                .Skip(Math.Max(0, query.Offset))
                .Take(query.Limit > 0 ? query.Limit : 20)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetShortTermPromotionCandidateReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<PromotionCandidateReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<PromotionCandidateReviewRecord>();
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetShortTermPromotionCandidateReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<PromotionCandidateReviewRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)));
            }

            return results
                .OrderByDescending(item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
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
                            .Where(item =>
                                File.Exists(
                                    _paths.GetShortTermPromotionCandidatesJsonlPath(item.WorkspaceId,
                                        item.CollectionId!)))
                            .Select(item => new ShortTermMemoryScope
                            {
                                WorkspaceId = item.WorkspaceId,
                                CollectionId = item.CollectionId
                            })
                    ];
                })
                .DistinctBy(item => $"{item.WorkspaceId}\u001f{item.CollectionId}", StringComparer.OrdinalIgnoreCase)
        ];
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
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        ];
    }

    private static bool Matches(ShortTermPromotionCandidate item, ShortTermPromotionCandidateQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (query.Status is null || item.Status == query.Status.Value);
    }

    private static ShortTermPromotionCandidate Normalize(ShortTermPromotionCandidate item)
    {
        return new ShortTermPromotionCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(item.CandidateId) ? Guid.NewGuid().ToString("N") : item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceWorkingItemId = item.SourceWorkingItemId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            SuggestedTargetLayer = item.SuggestedTargetLayer,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = item.EvidenceRefs.ToArray(),
            Tags = item.Tags.ToArray(),
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Status = item.Status,
            DedupeKey = item.DedupeKey,
            SourceFingerprint = item.SourceFingerprint,
            GeneratedBy = item.GeneratedBy,
            PolicyVersion = item.PolicyVersion,
            RuleName = item.RuleName,
            RuleVersion = item.RuleVersion,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermPromotionCandidate Clone(ShortTermPromotionCandidate item)
    {
        return Normalize(item);
    }

    private static PromotionCandidateReviewRecord Normalize(PromotionCandidateReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new PromotionCandidateReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            TargetItemId = item.TargetItemId,
            TargetItemKind = item.TargetItemKind,
            TargetLayer = item.TargetLayer,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    private static PromotionCandidateReviewRecord Clone(PromotionCandidateReviewRecord item)
    {
        return Normalize(item);
    }
}
