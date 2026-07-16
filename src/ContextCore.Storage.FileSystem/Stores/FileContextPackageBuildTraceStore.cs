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

        await _jsonLines.AppendAsync(path, result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var traces = await ReadTraceFilesAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        var count = take > 0 ? take : 50;

        return [.. traces
            .OrderByDescending(item => item.CreatedAt)
            .Take(count)];
    }

    private async Task<IReadOnlyList<ContextPackageBuildResult>> ReadTraceFilesAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var results = new List<ContextPackageBuildResult>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateTraceFiles(workspaceId, collectionId))
        {
            var traces = await _jsonLines.ReadAsync<ContextPackageBuildResult>(path, cancellationToken)
                .ConfigureAwait(false);
            foreach (var trace in traces)
            {
                var key = string.IsNullOrWhiteSpace(trace.BuildId) ? trace.Package.PackageId : trace.BuildId;
                if (string.IsNullOrWhiteSpace(key) || keys.Add(key))
                {
                    results.Add(trace);
                }
            }
        }

        return results;
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
