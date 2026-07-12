using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的 <see cref="IContextStore"/> 与 <see cref="IContextCollectionStore"/> 实现。
/// 集合元数据以 JSON 文件保存，条目内容单独存储并通过 JSONL 元数据索引管理。
/// </summary>
/// <remarks>
/// P5-5: 读路径无锁（原子文件替换保证一致性），写路径保留 SemaphoreSlim 串行读改写。
/// collection 级 metadata cache 带文件 mtime 失效检测，避免重复全量扫描 items.jsonl。
/// </remarks>
public sealed class FileContextStore : IContextStore, IContextCollectionStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

    // P5-5: collection 级 immutable metadata cache, keyed by items.jsonl path.
    // Invalidation: file mtime mismatch → cache miss → re-read.
    private readonly ConcurrentDictionary<string, MetadataCacheEntry> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record MetadataCacheEntry(
        DateTime LastWriteUtc,
        IReadOnlyList<ContextItemMetadata> Metadata);

    public FileContextStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileContextStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _serializer = serializer;
        _reader = new FileSystemReader();
        _writer = new FileSystemWriter();
    }

    public async Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateRequiredIds(item.WorkspaceId, item.CollectionId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.GetItemsDirectory(item.WorkspaceId, item.CollectionId));
            Directory.CreateDirectory(_paths.GetRawDirectory(item.WorkspaceId, item.CollectionId));
            await EnsureCollectionFileAsync(item.WorkspaceId, item.CollectionId, cancellationToken).ConfigureAwait(false);

            var existingMetadata = await ReadItemMetadataAsync(
                item.WorkspaceId,
                item.CollectionId,
                cancellationToken).ConfigureAwait(false);

            var previous = existingMetadata.FirstOrDefault(metadata => metadata.Id == item.Id);
            if (previous is not null && previous.ContentFormat != item.ContentFormat)
            {
                var previousRawPath = _paths.GetRawContentPath(
                    previous.WorkspaceId,
                    previous.CollectionId,
                    previous.Id,
                    previous.ContentFormat);

                await _writer.DeleteIfExistsAsync(previousRawPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            var rawPath = _paths.GetRawContentPath(
                item.WorkspaceId,
                item.CollectionId,
                item.Id,
                item.ContentFormat);

            await _writer.WriteAllTextAtomicAsync(rawPath, item.Content, cancellationToken)
                .ConfigureAwait(false);

            var updatedMetadata = existingMetadata
                .Where(metadata => metadata.Id != item.Id)
                .Append(ContextItemMetadata.FromItem(item))
                .OrderBy(metadata => metadata.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await WriteItemMetadataAsync(
                item.WorkspaceId,
                item.CollectionId,
                updatedMetadata,
                cancellationToken).ConfigureAwait(false);

            InvalidateMetadataCache(item.WorkspaceId, item.CollectionId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        // P5-5: no read lock — atomic file replacement guarantees consistent reads
        var metadata = await ReadItemMetadataAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);

        var match = metadata.FirstOrDefault(item => item.Id == id);

        return match is null
            ? null
            : await MaterializeAsync(match, includeContent: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.WorkspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(query));
        }

        // P5-5: no read lock — atomic file replacement guarantees consistent reads.
        // Two-phase query: filter+paginate metadata first, read content only for final candidates.
        var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);
        var hasQueryText = !string.IsNullOrWhiteSpace(query.QueryText);

        // Phase 1: filter by metadata (no content I/O)
        var candidates = new List<ContextItemMetadata>();
        foreach (var collectionId in collectionIds)
        {
            var metadataEntries = await ReadItemMetadataAsync(
                query.WorkspaceId,
                collectionId,
                cancellationToken).ConfigureAwait(false);

            foreach (var metadata in metadataEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MatchesMetadata(metadata, query))
                {
                    candidates.Add(metadata);
                }
            }
        }

        var skip = Math.Max(0, query.Skip);
        var take = query.Take > 0 ? query.Take : 50;

        // Phase 2a: query text requires content — read all candidates, filter by text, then paginate
        if (hasQueryText)
        {
            var textFiltered = new List<ContextItem>();
            foreach (var metadata in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = await MaterializeAsync(metadata, includeContent: true, cancellationToken)
                    .ConfigureAwait(false);
                if (MatchesQueryText(item, query.QueryText))
                {
                    textFiltered.Add(item);
                }
            }

            return textFiltered
                .OrderByDescending(item => item.UpdatedAt)
                .Skip(skip)
                .Take(take)
                .Select(item => query.IncludeContent ? item : WithoutContent(item))
                .ToArray();
        }

        // Phase 2b: no query text — paginate metadata first, read content only for the final page
        var page = candidates
            .OrderByDescending(metadata => metadata.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .ToArray();

        var results = new List<ContextItem>(page.Length);
        foreach (var metadata in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await MaterializeAsync(metadata, includeContent: query.IncludeContent, cancellationToken)
                .ConfigureAwait(false);
            results.Add(item);
        }

        return results;
    }

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataEntries = await ReadItemMetadataAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);

            var match = metadataEntries.FirstOrDefault(metadata => metadata.Id == id);
            if (match is not null)
            {
                var rawPath = _paths.GetRawContentPath(workspaceId, collectionId, id, match.ContentFormat);
                await _writer.DeleteIfExistsAsync(rawPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            var updatedMetadata = metadataEntries
                .Where(metadata => metadata.Id != id)
                .ToArray();

            await WriteItemMetadataAsync(
                workspaceId,
                collectionId,
                updatedMetadata,
                cancellationToken).ConfigureAwait(false);

            InvalidateMetadataCache(workspaceId, collectionId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveCollectionAsync(
        ContextCollection collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ValidateRequiredIds(collection.WorkspaceId, collection.Id);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.GetCollectionDirectory(collection.WorkspaceId, collection.Id));

            var json = _serializer.SerializeCollection(collection);
            await _writer.WriteAllTextAtomicAsync(
                _paths.GetCollectionFilePath(collection.WorkspaceId, collection.Id),
                json,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        // P5-5: no read lock — atomic file replacement guarantees consistent reads
        var path = _paths.GetCollectionFilePath(workspaceId, collectionId);
        var json = await _reader.ReadAllTextAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(json)
            ? null
            : _serializer.DeserializeCollection(json);
    }

    private async Task EnsureCollectionFileAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var collectionPath = _paths.GetCollectionFilePath(workspaceId, collectionId);
        if (_reader.Exists(collectionPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var collection = new ContextCollection
        {
            Id = collectionId,
            WorkspaceId = workspaceId,
            Name = collectionId,
            CreatedAt = now,
            UpdatedAt = now
        };

        var json = _serializer.SerializeCollection(collection);
        await _writer.WriteAllTextAtomicAsync(collectionPath, json, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ContextItemMetadata>> ReadItemMetadataAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetItemsJsonlPath(workspaceId, collectionId);

        // P5-5: check metadata cache with mtime invalidation
        var lastWrite = TryGetLastWriteUtc(path);
        if (lastWrite is not null
            && _metadataCache.TryGetValue(path, out var cached)
            && cached.LastWriteUtc == lastWrite.Value)
        {
            return cached.Metadata;
        }

        var lines = await _reader.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);

        var metadata = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => _serializer.DeserializeItemMetadata(line))
            .Where(m => m is not null)
            .Cast<ContextItemMetadata>()
            .ToArray();

        if (lastWrite is not null)
        {
            _metadataCache[path] = new MetadataCacheEntry(lastWrite.Value, metadata);
        }

        return metadata;
    }

    private void InvalidateMetadataCache(string workspaceId, string collectionId)
    {
        var path = _paths.GetItemsJsonlPath(workspaceId, collectionId);
        _metadataCache.TryRemove(path, out _);
    }

    private static DateTime? TryGetLastWriteUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private async Task WriteItemMetadataAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ContextItemMetadata> metadataEntries,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetItemsJsonlPath(workspaceId, collectionId);
        var lines = metadataEntries.Select(_serializer.SerializeItemMetadata).ToArray();

        await _writer.WriteAllLinesAtomicAsync(path, lines, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContextItem> MaterializeAsync(
        ContextItemMetadata metadata,
        bool includeContent,
        CancellationToken cancellationToken)
    {
        if (!includeContent)
        {
            return metadata.ToContextItem(string.Empty);
        }

        var rawPath = _paths.GetRawContentPath(
            metadata.WorkspaceId,
            metadata.CollectionId,
            metadata.Id,
            metadata.ContentFormat);

        var content = await _reader.ReadAllTextAsync(rawPath, cancellationToken).ConfigureAwait(false)
            ?? string.Empty;

        return metadata.ToContextItem(content);
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
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(collectionsDirectory)
                .Select(Path.GetFileName)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
        ];
    }

    private static bool MatchesMetadata(ContextItemMetadata metadata, ContextQuery query)
    {
        return !IsExcluded(metadata, query)
            && MatchesTags(metadata.Tags, query.Tags)
            && MatchesTypes(metadata.Type, query.Types)
            && MatchesRefs(metadata, query.Refs);
    }

    private static bool IsExcluded(ContextItemMetadata metadata, ContextQuery query)
    {
        if (query.ExcludedIds.Any(id => string.Equals(id, metadata.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (query.ExcludedTypes.Any(type => string.Equals(type, metadata.Type, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !query.IncludeDerived
            && metadata.Metadata.TryGetValue("isDerived", out var isDerived)
            && string.Equals(isDerived, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTags(IReadOnlyList<string> itemTags, IReadOnlyList<string> queryTags)
    {
        if (queryTags.Count == 0)
        {
            return true;
        }

        var itemTagSet = itemTags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return queryTags.All(itemTagSet.Contains);
    }

    private static bool MatchesTypes(string itemType, IReadOnlyList<string> queryTypes)
    {
        return queryTypes.Count == 0
            || queryTypes.Any(type => string.Equals(type, itemType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesRefs(ContextItemMetadata metadata, IReadOnlyList<string> queryRefs)
    {
        if (queryRefs.Count == 0)
        {
            return true;
        }

        var refs = metadata.Refs
            .Concat(metadata.SourceRefs)
            .Append(metadata.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return queryRefs.Any(refs.Contains);
    }

    private static bool MatchesQueryText(ContextItem item, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        return Contains(item.Title, queryText)
            || Contains(item.Type, queryText)
            || Contains(item.Content, queryText)
            || item.Tags.Any(tag => Contains(tag, queryText));
    }

    private static bool Contains(string? value, string queryText)
    {
        return value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static ContextItem WithoutContent(ContextItem item)
    {
        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = string.Empty,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            Refs = item.Refs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            Importance = item.Importance,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static void ValidateRequiredIds(string workspaceId, string collectionId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            throw new ArgumentException("CollectionId is required.", nameof(collectionId));
        }
    }
}


