using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 vector lifecycle metadata review candidate 存储；仅保存人工 review 队列。</summary>
public sealed class FileVectorLifecycleMetadataReviewCandidateStore : IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileVectorLifecycleMetadataReviewCandidateStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleMetadataReviewCandidateStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task SaveAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = CandidateRecordNormalizer.Normalize(candidate);
        var path = _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _jsonLines.UpsertAsync(
            path,
            normalized,
            item => item.CandidateId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath))
        {
            var path = _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var candidates = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewCandidate>(path, cancellationToken)
                .ConfigureAwait(false);
            var match = candidates.FirstOrDefault(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return CandidateRecordNormalizer.Clone(match);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<VectorLifecycleMetadataReviewCandidate>();
        foreach (var scope in _scopeCatalog.ResolveScopes(query.WorkspaceId, query.CollectionId, _paths.GetVectorLifecycleMetadataReviewCandidatesJsonlPath))
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
                .Select(CandidateRecordNormalizer.Clone)
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
}
