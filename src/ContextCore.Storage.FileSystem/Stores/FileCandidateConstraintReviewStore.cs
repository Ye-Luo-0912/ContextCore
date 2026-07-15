using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 CandidateConstraint 审核记录存储。</summary>
public sealed class FileCandidateConstraintReviewStore : ICandidateConstraintReviewStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileCandidateConstraintReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task AppendReviewAsync(
        CandidateConstraintReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("CandidateConstraint review 必须包含 collectionId。", nameof(record));
        }

        var path = _paths.GetCandidateConstraintReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<CandidateConstraintReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);

        var results = new List<CandidateConstraintReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetCandidateConstraintReviewsJsonlPath))
        {
            var path = _paths.GetCandidateConstraintReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<CandidateConstraintReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => string.Equals(item.ConstraintId, constraintId, StringComparison.OrdinalIgnoreCase)));
        }

        return results
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();
    }
}
