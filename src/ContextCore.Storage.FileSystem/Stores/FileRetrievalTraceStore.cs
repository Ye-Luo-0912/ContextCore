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
    // retention 现为 fire-and-forget，保留最近一次清理 Task 供测试观察完成。
    private Task? _pendingPurge;

    /// <summary>最近一次 MaybePurge 派发的清理 Task（已禁用/未到期时为 CompletedTask）。供测试等待清理完成。</summary>
    internal Task? PendingPurge => _pendingPurge;

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

        // retention 移出 Save 热路径——fire-and-forget，不阻塞写入返回。
        _pendingPurge = _janitor.MaybePurge(_paths.GetRetrievalTraceDirectory(trace.WorkspaceId, trace.CollectionId));
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

    public async Task<ContextRetrievalTrace?> GetAsync(
        string workspaceId,
        string collectionId,
        string retrievalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(retrievalId))
        {
            return null;
        }

        // 稳定主键点查：全量过滤匹配 retrieval_id（append-only 分片文件无索引，
        // 按需全读；审计路径低频，数据量受 retention 约束）。
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var traces = await TraceQueryHelper.ReadRecentAsync<ContextRetrievalTrace>(
            paths,
            int.MaxValue,
            _jsonLines,
            t => t.RetrievalId ?? string.Empty,
            filter: t => string.Equals(t.RetrievalId, retrievalId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

        return traces.FirstOrDefault();
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
