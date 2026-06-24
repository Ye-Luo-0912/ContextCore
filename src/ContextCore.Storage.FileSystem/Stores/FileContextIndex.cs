using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IContextIndex"/> 实现，索引条目持久化为 JSONL 文件。</summary>
public sealed class FileContextIndex : IContextIndex
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

    public FileContextIndex(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextIndex(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _serializer = serializer;
        _reader = new FileSystemReader();
        _writer = new FileSystemWriter();
    }

    public async Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.WorkspaceId) || string.IsNullOrWhiteSpace(entry.CollectionId))
        {
            throw new ArgumentException("WorkspaceId and CollectionId are required.", nameof(entry));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.GetIndexDirectory(entry.WorkspaceId, entry.CollectionId));

            var existingEntries = await ReadEntriesAsync(
                entry.WorkspaceId,
                entry.CollectionId,
                cancellationToken).ConfigureAwait(false);

            var updatedEntries = existingEntries
                .Where(existing => existing.Id != entry.Id)
                .Append(entry)
                .OrderBy(existing => existing.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await WriteEntriesAsync(
                entry.WorkspaceId,
                entry.CollectionId,
                updatedEntries,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // TODO-DEMO [P0-3]：当前仅支持关键词 Contains 匹配，不支持语义向量搜索。
    // 生产使用前需接入 embedding 模型，将向量持久化并实现相似度检索。参见：TODO.md → P0-3
    public async Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.WorkspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(query));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);
            var results = new List<ContextIndexEntry>();

            foreach (var collectionId in collectionIds)
            {
                var entries = await ReadEntriesAsync(query.WorkspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);

                results.AddRange(entries.Where(entry => Matches(entry, query)));
            }

            var take = query.Take > 0 ? query.Take : 50;

            return [.. results
                .OrderByDescending(entry => entry.Weight)
                .ThenByDescending(entry => entry.CreatedAt)
                .Take(take)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ContextIndexEntry>> ReadEntriesAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var lines = await _reader.ReadAllLinesAsync(
                _paths.GetIndexJsonlPath(workspaceId, collectionId),
                cancellationToken)
            .ConfigureAwait(false);

        return [.. lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => _serializer.DeserializeIndexEntry(line))
            .Where(entry => entry is not null)
            .Cast<ContextIndexEntry>()];
    }

    private async Task WriteEntriesAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ContextIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetIndexJsonlPath(workspaceId, collectionId);
        var lines = entries.Select(_serializer.SerializeIndexEntry).ToArray();

        await _writer.WriteAllLinesAtomicAsync(path, lines, cancellationToken)
            .ConfigureAwait(false);
    }

    private IReadOnlyList<string> ResolveCollectionIds(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [collectionId];
        }

        var collectionsDirectory = _paths.GetCollectionsDirectory(workspaceId);
        if (!Directory.Exists(collectionsDirectory))
        {
            return Array.Empty<string>();
        }

        return [.. Directory.EnumerateDirectories(collectionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()];
    }

    private static bool Matches(ContextIndexEntry entry, IndexQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.CollectionId)
            && !string.Equals(entry.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.Key)
            && !entry.Key.Contains(query.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.Kind)
            && !string.Equals(entry.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Tags.Count > 0)
        {
            var tagSet = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return string.Equals(entry.Kind, "tag", StringComparison.OrdinalIgnoreCase)
                && tagSet.Contains(entry.Key);
        }

        return true;
    }
}
