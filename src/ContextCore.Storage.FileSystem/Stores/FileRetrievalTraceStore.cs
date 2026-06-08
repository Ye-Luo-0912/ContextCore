using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>将混合检索 trace 持久化为集合目录下的 JSONL 文件。</summary>
public sealed class FileRetrievalTraceStore : IRetrievalTraceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileRetrievalTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileRetrievalTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        ContextRetrievalTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var path = _paths.GetRetrievalTraceJsonlPath(trace.WorkspaceId, trace.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                path,
                trace,
                item => string.IsNullOrWhiteSpace(item.RetrievalId) ? Guid.NewGuid().ToString("N") : item.RetrievalId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetRetrievalTraceJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var traces = await _jsonLines.ReadAsync<ContextRetrievalTrace>(path, cancellationToken)
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
