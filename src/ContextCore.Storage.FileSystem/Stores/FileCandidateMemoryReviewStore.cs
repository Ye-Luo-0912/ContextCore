using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 CandidateMemory review / cleanup 审核历史存储。</summary>
public sealed class FileCandidateMemoryReviewStore : ICandidateMemoryReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileCandidateMemoryReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task AppendReviewAsync(
        CandidateMemoryReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("CandidateMemory review 必须包含 collectionId。", nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetCandidateMemoryReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await ReadReviewsWithLegacyAsync(normalized.WorkspaceId, normalized.CollectionId, cancellationToken)
                .ConfigureAwait(false);
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

    public async Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<CandidateMemoryReviewRecord>();
            foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetCandidateMemoryReviewsJsonlPath, _paths.GetLegacyCandidateMemoryReviewsJsonlPath))
            {
                var items = await ReadReviewsWithLegacyAsync(scope.WorkspaceId, scope.CollectionId, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)));
            }

            return results
                .OrderByDescending(static item => item.CreatedAt)
                .Select(ReviewRecordNormalizer.Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CandidateMemoryReviewRecord>> ReadReviewsWithLegacyAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var primaryPath = _paths.GetCandidateMemoryReviewsJsonlPath(workspaceId, collectionId);
        var primary = await _jsonLines.ReadAsync<CandidateMemoryReviewRecord>(primaryPath, cancellationToken)
            .ConfigureAwait(false);
        var legacyPath = _paths.GetLegacyCandidateMemoryReviewsJsonlPath(workspaceId, collectionId);
        if (string.Equals(primaryPath, legacyPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacyPath))
        {
            return primary;
        }

        var legacy = await _jsonLines.ReadAsync<CandidateMemoryReviewRecord>(legacyPath, cancellationToken)
            .ConfigureAwait(false);
        if (legacy.Count == 0)
        {
            return primary;
        }

        var keys = primary
            .Where(item => !string.IsNullOrWhiteSpace(item.ReviewId))
            .Select(item => item.ReviewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. primary,
            .. legacy.Where(item => string.IsNullOrWhiteSpace(item.ReviewId) || keys.Add(item.ReviewId))
        ];
    }
}
