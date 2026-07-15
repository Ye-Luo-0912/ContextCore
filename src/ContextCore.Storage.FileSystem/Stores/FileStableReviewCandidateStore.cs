using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Stable review 候选项存储。</summary>
public sealed class FileStableReviewCandidateStore : IStableReviewCandidateStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileStableReviewCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileStableReviewCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task SaveAsync(StableReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = CandidateRecordNormalizer.Normalize(candidate);

        var path = _paths.GetStableReviewCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<StableReviewCandidate>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.StableReviewCandidateId, normalized.StableReviewCandidateId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetStableReviewCandidatesJsonlPath))
        {
            var path = _paths.GetStableReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var candidates = await _jsonLines.ReadAsync<StableReviewCandidate>(path, cancellationToken).ConfigureAwait(false);
            var match = candidates.FirstOrDefault(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return CandidateRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<StableReviewCandidate>();
        foreach (var scope in _scopeCatalog.ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetStableReviewCandidatesJsonlPath))
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
                .Select(CandidateRecordNormalizer.Clone)
        ];
    }

    public async Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);

        var path = _paths.GetStableReviewCandidateReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<StableReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        var results = new List<StableReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetStableReviewCandidateReviewsJsonlPath))
        {
            var path = _paths.GetStableReviewCandidateReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<StableReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase)));
        }

        return
        [
            .. results
                .OrderByDescending(static item => item.CreatedAt)
                .Select(ReviewRecordNormalizer.Clone)
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
}
