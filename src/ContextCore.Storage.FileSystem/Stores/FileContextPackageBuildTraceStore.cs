using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 将上下文包构建 trace 持久化为按日期分片的 JSONL 文件。
/// 写入使用 append-only 语义（构建 trace 是事件流，每次 Save 都是新记录），
/// 避免旧 Upsert 实现的"读全部→反序列化→重新序列化→原子重写整个文件"开销。
/// </summary>
public sealed class FileContextPackageBuildTraceStore : IContextPackageBuildTraceStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileTraceJanitor _janitor;
    private Task? _pendingPurge;

    /// <summary>最近一次 MaybePurge 派发的清理 Task。供测试等待清理完成。</summary>
    internal Task? PendingPurge => _pendingPurge;

    public FileContextPackageBuildTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer(), options)
    {
    }

    public FileContextPackageBuildTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
        : this(paths, serializer, new FileStorageOptions())
    {
    }

    internal FileContextPackageBuildTraceStore(FilePathResolver paths, FileFormatSerializer serializer, FileStorageOptions options)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _janitor = new FileTraceJanitor(options);
    }

    public async Task SaveAsync(
        ContextPackageBuildResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var path = _paths.GetPackageBuildTraceJsonlPath(result.Package.WorkspaceId, result.Package.CollectionId);

        await _jsonLines.AppendAsync(path, result, cancellationToken).ConfigureAwait(false);

        // retention 移出 Save 热路径——fire-and-forget，不阻塞写入返回。
        _pendingPurge = _janitor.MaybePurge(_paths.GetPackageBuildTraceDirectory(result.Package.WorkspaceId, result.Package.CollectionId));
    }

    public async Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var traces = await TraceQueryHelper.ReadRecentAsync<ContextPackageBuildResult>(
            paths,
            take,
            _jsonLines,
            t => string.IsNullOrWhiteSpace(t.BuildId) ? (t.Package.PackageId ?? string.Empty) : t.BuildId,
            filter: null,
            cancellationToken).ConfigureAwait(false);

        var count = take > 0 ? take : 50;
        return [.. traces.OrderByDescending(item => item.CreatedAt).Take(count)];
    }

    private IReadOnlyList<string> EnumerateTraceFiles(string workspaceId, string collectionId)
    {
        var files = new List<string>();
        var directory = _paths.GetPackageBuildTraceDirectory(workspaceId, collectionId);
        if (Directory.Exists(directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        var legacyPath = _paths.GetLegacyPackageBuildTraceJsonlPath(workspaceId, collectionId);
        if (File.Exists(legacyPath))
        {
            files.Add(legacyPath);
        }

        return files;
    }
}
