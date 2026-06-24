using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 lifecycle metadata review 历史存储；只记录人工决策。</summary>
public sealed class FileVectorLifecycleMetadataReviewStore : IVectorLifecycleMetadataReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileVectorLifecycleMetadataReviewStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleMetadataReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        VectorLifecycleMetadataReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewRecord>(path, cancellationToken)
                .ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.ReviewedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<VectorLifecycleMetadataReviewRecord>();
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var records = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewRecord>(path, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(records.Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)));
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.ReviewedAt)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<VectorLifecycleMetadataReviewRecord>();
            foreach (var scope in ResolveScopes(workspaceId, collectionId))
            {
                var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var records = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewRecord>(path, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(records);
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.ReviewedAt)
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
                    var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                    if (string.IsNullOrWhiteSpace(workspaceId) || !Directory.Exists(collectionsRoot))
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
                            .Where(item => File.Exists(_paths.GetVectorLifecycleMetadataReviewsJsonlPath(item.WorkspaceId, item.CollectionId!)))
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

    private static VectorLifecycleMetadataReviewRecord Normalize(VectorLifecycleMetadataReviewRecord record)
        => new()
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            CandidateId = record.CandidateId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            MustHitItemId = record.MustHitItemId,
            Decision = record.Decision,
            ResultStatus = record.ResultStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ProposedLifecycle = record.ProposedLifecycle,
            ProposedReviewStatus = record.ProposedReviewStatus,
            ProposedTargetSection = record.ProposedTargetSection,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            SidecarWritten = record.SidecarWritten,
            UnsafeApprovalBlocked = record.UnsafeApprovalBlocked,
            BlockedReason = record.BlockedReason,
            ReviewedAt = record.ReviewedAt == default ? DateTimeOffset.UtcNow : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleMetadataReviewRecord Clone(VectorLifecycleMetadataReviewRecord record)
        => Normalize(record);
}
