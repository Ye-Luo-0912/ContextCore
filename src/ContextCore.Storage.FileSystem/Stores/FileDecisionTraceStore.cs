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

    public FileDecisionTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileDecisionTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = _paths.GetDecisionTraceJsonlPath(record.WorkspaceId, record.CollectionId);

        await _jsonLines.AppendAsync(path, record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var records = await ReadTraceFilesAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        var count = take > 0 ? take : 50;

        return [.. records
            .OrderByDescending(item => item.CreatedAt)
            .Take(count)];
    }

    private async Task<IReadOnlyList<ContextDecisionRecord>> ReadTraceFilesAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var results = new List<ContextDecisionRecord>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateTraceFiles(workspaceId, collectionId))
        {
            var records = await _jsonLines.ReadAsync<ContextDecisionRecord>(path, cancellationToken)
                .ConfigureAwait(false);
            foreach (var record in records)
            {
                // legacy 单文件路径可能混入其他 ws/col 的记录，需要运行时过滤
                if (!string.Equals(record.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(record.DecisionId) ? Guid.NewGuid().ToString("N") : record.DecisionId;
                if (keys.Add(key))
                {
                    results.Add(record);
                }
            }
        }

        return results;
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
