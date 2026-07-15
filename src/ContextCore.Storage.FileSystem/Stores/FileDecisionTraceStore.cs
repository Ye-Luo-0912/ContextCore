using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>将统一决策记录（V17.0 decision trace）持久化为集合目录下的 JSONL 文件。</summary>
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

        await _jsonLines.UpsertAsync(
            path,
            record,
            item => string.IsNullOrWhiteSpace(item.DecisionId) ? Guid.NewGuid().ToString("N") : item.DecisionId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetDecisionTraceJsonlPath(workspaceId, collectionId);
        if (!File.Exists(path))
        {
            return Array.Empty<ContextDecisionRecord>();
        }

        var records = await _jsonLines.ReadAsync<ContextDecisionRecord>(path, cancellationToken)
            .ConfigureAwait(false);
        var count = take > 0 ? take : 50;

        return [.. records
            .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Take(count)];
    }
}
