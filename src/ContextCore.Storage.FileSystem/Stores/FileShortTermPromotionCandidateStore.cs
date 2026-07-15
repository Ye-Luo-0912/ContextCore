using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的短期晋升候选项存储。</summary>
public sealed class FileShortTermPromotionCandidateStore : IShortTermPromotionCandidateStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileShortTermPromotionCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = CandidateRecordNormalizer.Normalize(candidate);

        var path = _paths.GetShortTermPromotionCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<ShortTermPromotionCandidate>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.CandidateId, normalized.CandidateId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermPromotionCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetShortTermPromotionCandidatesJsonlPath))
        {
            var path = _paths.GetShortTermPromotionCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<ShortTermPromotionCandidate>(path, cancellationToken).ConfigureAwait(false);
            var match = items.FirstOrDefault(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return CandidateRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scopes = _scopeCatalog.ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetShortTermPromotionCandidatesJsonlPath);
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
            .Select(CandidateRecordNormalizer.Clone)
            .ToArray();
    }

    public async Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);

        var path = _paths.GetShortTermPromotionCandidateReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<PromotionCandidateReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        var results = new List<PromotionCandidateReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetShortTermPromotionCandidatesJsonlPath))
        {
            var path = _paths.GetShortTermPromotionCandidateReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<PromotionCandidateReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)));
        }

        return results
            .OrderByDescending(item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();
    }

    private static bool Matches(ShortTermPromotionCandidate item, ShortTermPromotionCandidateQuery query)
    {
        return string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (query.Status is null || item.Status == query.Status.Value);
    }
}
