using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>将上下文包构建 trace 持久化为集合目录下的 JSONL 文件。</summary>
public sealed class FileContextPackageBuildTraceStore : IContextPackageBuildTraceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileContextPackageBuildTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextPackageBuildTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        ContextPackageBuildResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var path = _paths.GetPackageBuildTraceJsonlPath(result.Package.WorkspaceId, result.Package.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                path,
                result,
                item => string.IsNullOrWhiteSpace(item.BuildId) ? item.Package.PackageId : item.BuildId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetPackageBuildTraceJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var traces = await _jsonLines.ReadAsync<ContextPackageBuildResult>(path, cancellationToken)
                .ConfigureAwait(false);
            var count = take > 0 ? take : 50;

            return [.. traces
                .OrderByDescending(item => item.CreatedAt)
                .Take(count)];
        }
        finally
        {
            _gate.Release();
        }
    }
}
