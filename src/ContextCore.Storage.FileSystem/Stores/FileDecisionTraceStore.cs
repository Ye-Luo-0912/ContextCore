using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 将统一决策记录（V17.0 decision trace）持久化为按日期分片的 JSONL 文件。
/// 写入使用 append-only 语义（决策记录是事件流，每次 Save 都是新记录），
/// 避免旧 Upsert 实现的"读全部→反序列化→重新序列化→原子重写整个文件"开销。
/// </summary>
public sealed class FileDecisionTraceStore : IDecisionTraceStore
{
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;
    private readonly FileTraceJanitor _janitor;
    private Task? _pendingPurge;

    /// <summary>最近一次 MaybePurge 派发的清理 Task。供测试等待清理完成。</summary>
    internal Task? PendingPurge => _pendingPurge;

    public FileDecisionTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer(), options)
    {
    }

    public FileDecisionTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
        : this(paths, serializer, new FileStorageOptions())
    {
    }

    internal FileDecisionTraceStore(FilePathResolver paths, FileFormatSerializer serializer, FileStorageOptions options)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _janitor = new FileTraceJanitor(options);
    }

    public async Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = _paths.GetDecisionTraceJsonlPath(record.WorkspaceId, record.CollectionId);

        await _jsonLines.AppendAsync(path, record, cancellationToken).ConfigureAwait(false);

        // retention 移出 Save 热路径——fire-and-forget，不阻塞写入返回。
        _pendingPurge = _janitor.MaybePurge(_paths.GetDecisionTraceDirectory(record.WorkspaceId, record.CollectionId));
    }

    public async Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var paths = EnumerateTraceFiles(workspaceId, collectionId);
        var records = await TraceQueryHelper.ReadRecentAsync<ContextDecisionRecord>(
            paths,
            take,
            _jsonLines,
            r => r.DecisionId ?? string.Empty,
            r => string.Equals(r.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(r.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

        var count = take > 0 ? take : 50;
        return [.. records.OrderByDescending(item => item.CreatedAt).Take(count)];
    }

    private IReadOnlyList<string> EnumerateTraceFiles(string workspaceId, string collectionId)
    {
        var files = new List<string>();
        var directory = _paths.GetDecisionTraceDirectory(workspaceId, collectionId);
        if (Directory.Exists(directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        var legacyPath = _paths.GetLegacyDecisionTraceJsonlPath(workspaceId, collectionId);
        if (File.Exists(legacyPath))
        {
            files.Add(legacyPath);
        }

        return files;
    }
}
