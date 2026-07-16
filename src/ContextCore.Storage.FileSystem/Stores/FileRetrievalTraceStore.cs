using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 将混合检索 trace 持久化为按日期分片的 JSONL 文件。
/// 写入使用 append-only 语义（检索 trace 是事件流，每次 Save 都是新记录），
/// 避免旧 Upsert 实现的"读全部→反序列化→重新序列化→原子重写整个文件"开销。
/// </summary>
public sealed class FileRetrievalTraceStore : IRetrievalTraceStore
{
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;
    private readonly FileTraceJanitor _janitor;

    public FileRetrievalTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer(), options)
    {
    }

    public FileRetrievalTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
        : this(paths, serializer, new FileStorageOptions())
    {
    }

    internal FileRetrievalTraceStore(FilePathResolver paths, FileFormatSerializer serializer, FileStorageOptions options)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _janitor = new FileTraceJanitor(options);
    }

    public async Task SaveAsync(
        ContextRetrievalTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var path = _paths.GetRetrievalTraceJsonlPath(trace.WorkspaceId, trace.CollectionId);

        await _jsonLines.AppendAsync(path, trace, cancellationToken).ConfigureAwait(false);

        _janitor.MaybePurge(_paths.GetRetrievalTraceDirectory(trace.WorkspaceId, trace.CollectionId), cancellationToken);
    }

    public async Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var traces = await TraceQueryHelper.ReadRecentAsync<ContextRetrievalTrace>(
            paths,
            take,
            _jsonLines,
            t => t.RetrievalId ?? string.Empty,
            filter: null,
            cancellationToken).ConfigureAwait(false);

        var count = take > 0 ? take : 50;
        return [.. traces.OrderByDescending(item => item.CreatedAt).Take(count)];
    }

    private IReadOnlyList<string> EnumerateTraceFiles(string workspaceId, string collectionId)
    {
        var files = new List<string>();
        var directory = _paths.GetRetrievalTraceDirectory(workspaceId, collectionId);
        if (Directory.Exists(directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        var legacyPath = _paths.GetLegacyRetrievalTraceJsonlPath(workspaceId, collectionId);
        if (File.Exists(legacyPath))
        {
            files.Add(legacyPath);
        }

        return files;
    }
}
