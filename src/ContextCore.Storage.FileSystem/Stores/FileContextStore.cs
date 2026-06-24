using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的 <see cref="IContextStore"/> 与 <see cref="IContextCollectionStore"/> 实现。
/// 集合元数据以 JSON 文件保存，条目内容单独存储并通过 JSONL 元数据索引管理。
/// </summary>
public sealed class FileContextStore : IContextStore, IContextCollectionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await ReadItemMetadataAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);

            var match = metadata.FirstOrDefault(item => item.Id == id);

            return match is null
                ? null
                : await MaterializeAsync(match, includeContent: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);
            var results = new List<ContextItem>();

            foreach (var collectionId in collectionIds)
            {
                var metadataEntries = await ReadItemMetadataAsync(
                    query.WorkspaceId,
                    collectionId,
                    cancellationToken).ConfigureAwait(false);

                foreach (var metadata in metadataEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!MatchesMetadata(metadata, query))
                    {
                        continue;
                    }

                    var needsContent = query.IncludeContent || !string.IsNullOrWhiteSpace(query.QueryText);
                    var item = await MaterializeAsync(metadata, needsContent, cancellationToken)
                        .ConfigureAwait(false);

                    if (!MatchesQueryText(item, query.QueryText))
                    {
                        continue;
                    }

                    results.Add(query.IncludeContent ? item : WithoutContent(item));
                }
            }

            var skip = Math.Max(0, query.Skip);
            var take = query.Take > 0 ? query.Take : 50;

            return results
                .OrderByDescending(item => item.UpdatedAt)
                .Skip(skip)
                .Take(take)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCollectionAsync(
        ContextCollection collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ValidateRequiredIds(collection.WorkspaceId, collection.Id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
        }
    }

    public async Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetCollectionFilePath(workspaceId, collectionId);
            var json = await _reader.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(json)
                ? null
                : _serializer.DeserializeCollection(json);
        }
        finally
        {
            _gate.Release();
        }
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
        var lines = await _reader.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => _serializer.DeserializeItemMetadata(line))
            .Where(metadata => metadata is not null)
            .Cast<ContextItemMetadata>()
            .ToArray();
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


