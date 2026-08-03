using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的 <see cref="IContextStore"/> 与 <see cref="IContextCollectionStore"/> 实现。
/// 集合元数据以 JSON 文件保存，条目内容单独存储并通过 JSONL 元数据索引管理。
/// </summary>
/// <remarks>
/// -fix: 读路径恢复 SemaphoreSlim 保证内容与 metadata 跨文件一致性。
/// 原子替换只保证单文件完整，不保证 content+metadata 组成同一快照。
/// collection 级 metadata cache 带 mtime 双重校验和容量上限。
/// </remarks>
public sealed class FileContextStore : IContextStore, IContextCollectionStore, IContextStoreBatchLookup, IContextQueryPageStore
{
    private const int MaxCacheEntries = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

    // collection 级 immutable metadata cache, keyed by items.jsonl path.
    // -fix: mtime 双重校验（读前+读后），容量上限 256 防止无限增长。
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.GetItemsDirectory(item.WorkspaceId, item.CollectionId));
            Directory.CreateDirectory(_paths.GetRawDirectory(item.WorkspaceId, item.CollectionId));
            await EnsureCollectionFileAsync(item.WorkspaceId, item.CollectionId, cancellationToken).ConfigureAwait(false);

            var itemsPath = _paths.GetItemsJsonlPath(item.WorkspaceId, item.CollectionId);
            var ctx = new SaveContext();

            // 在跨进程写锁内执行完整 RMW——读取既有 metadata、计算更新、原子替换 metadata 文件、
            // 最后写入 raw content 副作用文件。锁路径即 items.jsonl 本身，
            // 确保两个进程不会各自读到旧 metadata 后互相覆盖（lost update）。
            await _writer.UpdateWithSideEffectsAsync(
                itemsPath,
                read: async ct =>
                {
                    ctx.ExistingMetadata = await ReadItemMetadataLockedAsync(
                        item.WorkspaceId, item.CollectionId, ct).ConfigureAwait(false);
                    return ctx;
                },
                modify: (c, ct) =>
                {
                    var previous = c.ExistingMetadata.FirstOrDefault(metadata => metadata.Id == item.Id);
                    if (previous is not null && previous.ContentFormat != item.ContentFormat)
                    {
                        c.PreviousRawPathToDelete = _paths.GetRawContentPath(
                            previous.WorkspaceId,
                            previous.CollectionId,
                            previous.Id,
                            previous.ContentFormat);
                    }

                    c.NewRawPath = _paths.GetRawContentPath(
                        item.WorkspaceId,
                        item.CollectionId,
                        item.Id,
                        item.ContentFormat);
                    c.NewRawContent = item.Content;

                    var updatedLines = c.ExistingMetadata
                        .Where(metadata => metadata.Id != item.Id)
                        .Append(ContextItemMetadata.FromItem(item))
                        .OrderBy(metadata => metadata.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(_serializer.SerializeItemMetadata)
                        .ToArray();

                    return Task.FromResult<IReadOnlyList<string>>(updatedLines);
                },
                write: async (c, ct) =>
                {
                    if (c.NewRawPath is not null && c.NewRawContent is not null)
                    {
                        await _writer.WriteAllTextAtomicAsync(c.NewRawPath, c.NewRawContent, ct)
                            .ConfigureAwait(false);
                    }

                    if (c.PreviousRawPathToDelete is not null)
                    {
                        await _writer.DeleteIfExistsAsync(c.PreviousRawPathToDelete, ct)
                            .ConfigureAwait(false);
                    }

                    InvalidateMetadataCache(item.WorkspaceId, item.CollectionId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// SaveAsync 的 RMW 上下文。ExistingMetadata 在 read 阶段填充，
    /// 其余字段在 modify 阶段填充、write 阶段消费（跨进程锁内原子完成）。
    /// </summary>
    private sealed class SaveContext
    {
        public IReadOnlyList<ContextItemMetadata> ExistingMetadata = Array.Empty<ContextItemMetadata>();
        public string? PreviousRawPathToDelete;
        public string? NewRawPath;
        public string? NewRawContent;
    }

    /// <summary>
    /// -fix: 读路径加锁，保证 metadata 与 content 跨文件一致性。
    /// </summary>
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
            var metadata = await ReadItemMetadataLockedAsync(workspaceId, collectionId, cancellationToken)
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

    /// <summary>
    /// 批量按 ID 获取上下文条目。在单次 _gate 锁内读取 metadata 并 materialize 所有匹配项，
    /// 避免 N 次单条 GetAsync 各自获取锁导致的串行退化。
    /// </summary>
    public async Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ValidateRequiredIds(workspaceId, collectionId);

        if (ids.Count == 0)
        {
            return Array.Empty<ContextItem>();
        }

        var idSet = new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0)
        {
            return Array.Empty<ContextItem>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await ReadItemMetadataLockedAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);

            var results = new List<ContextItem>(idSet.Count);
            foreach (var entry in metadata)
            {
                if (idSet.Contains(entry.Id))
                {
                    var item = await MaterializeAsync(entry, includeContent: true, cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(item);
                }
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// -fix: 读路径加锁，保证 metadata 与 content 跨文件一致性。
    /// Two-phase query: filter+paginate metadata first, read content only for final candidates.
    /// </summary>
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
            var hasQueryText = !string.IsNullOrWhiteSpace(query.QueryText);

            // Phase 1: filter by metadata (no content I/O)
            var candidates = new List<ContextItemMetadata>();
            foreach (var collectionId in collectionIds)
            {
                var metadataEntries = await ReadItemMetadataLockedAsync(
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

            var skip = query.After is null ? Math.Max(0, query.Skip) : 0;
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
                    .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                    .Where(item => IsAfterCursor(item, query.After))
                    .Skip(skip)
                    .Take(take)
                    .Select(item => query.IncludeContent ? item : WithoutContent(item))
                    .ToArray();
            }

            // Phase 2b: no query text — paginate metadata first, read content only for the final page
            var page = candidates
                .OrderByDescending(metadata => metadata.UpdatedAt)
                .ThenByDescending(metadata => metadata.Id, StringComparer.Ordinal)
                .Where(metadata => IsAfterCursor(metadata, query.After))
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
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ContextQueryPageResult> QueryPageAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 取 Take + 1 条判定 HasMore；返回前 Take 条，下一页游标取自末条排序键。
        var take = query.Take > 0 ? query.Take : 50;
        var fetchQuery = query.CloneWith(take: take + 1);
        var items = (await QueryAsync(fetchQuery, cancellationToken).ConfigureAwait(false)).ToList();

        var hasMore = items.Count > take;
        var pageItems = hasMore ? items.Take(take).ToList() : items;

        if (pageItems.Count == 0)
        {
            return new ContextQueryPageResult { Items = pageItems, HasMore = false, NextCursor = null };
        }

        // 文件存储按 (updated_at, id) 排序——等价于 ID 命中源（SourceOrder=1，无 ts_rank）。
        var last = pageItems[^1];
        var nextCursor = new ContextQueryCursor
        {
            SourceOrder = 1,
            TsRank = 0,
            Importance = last.Importance,
            UpdatedAt = last.UpdatedAt,
            Id = last.Id
        };

        return new ContextQueryPageResult
        {
            Items = pageItems,
            HasMore = hasMore,
            NextCursor = hasMore ? nextCursor : null
        };
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
            var itemsPath = _paths.GetItemsJsonlPath(workspaceId, collectionId);
            var ctx = new DeleteContext();

            // 跨进程锁内完整 RMW——读取 metadata 以决定 raw 删除路径、原子替换 metadata、删除 raw。
            await _writer.UpdateWithSideEffectsAsync(
                itemsPath,
                read: async ct =>
                {
                    ctx.ExistingMetadata = await ReadItemMetadataLockedAsync(
                        workspaceId, collectionId, ct).ConfigureAwait(false);
                    return ctx;
                },
                modify: (c, ct) =>
                {
                    var match = c.ExistingMetadata.FirstOrDefault(metadata => metadata.Id == id);
                    if (match is not null)
                    {
                        c.RawPathToDelete = _paths.GetRawContentPath(
                            workspaceId, collectionId, id, match.ContentFormat);
                    }

                    var updatedLines = c.ExistingMetadata
                        .Where(metadata => metadata.Id != id)
                        .Select(_serializer.SerializeItemMetadata)
                        .ToArray();

                    return Task.FromResult<IReadOnlyList<string>>(updatedLines);
                },
                write: async (c, ct) =>
                {
                    if (c.RawPathToDelete is not null)
                    {
                        await _writer.DeleteIfExistsAsync(c.RawPathToDelete, ct)
                            .ConfigureAwait(false);
                    }

                    InvalidateMetadataCache(workspaceId, collectionId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// DeleteAsync 的 RMW 上下文。ExistingMetadata 在 read 阶段填充，
    /// RawPathToDelete 在 modify 阶段填充、write 阶段消费。
    /// </summary>
    private sealed class DeleteContext
    {
        public IReadOnlyList<ContextItemMetadata> ExistingMetadata = Array.Empty<ContextItemMetadata>();
        public string? RawPathToDelete;
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

    /// <summary>单文件原子读写，无需跨文件一致性保证。</summary>
    public async Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredIds(workspaceId, collectionId);

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

    /// <summary>
    /// -fix: 调用方已持有 _gate 锁。带 mtime 缓存和读后复核。
    /// </summary>
    private async Task<IReadOnlyList<ContextItemMetadata>> ReadItemMetadataLockedAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetItemsJsonlPath(workspaceId, collectionId);

        // 检查缓存（调用方持锁，无竞态）
        var mtimeBefore = TryGetLastWriteUtc(path);
        if (mtimeBefore is not null
            && _metadataCache.TryGetValue(path, out var cached)
            && cached.LastWriteUtc == mtimeBefore.Value)
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

        // -fix: 读后复核 mtime；持锁期间不会有并发写，但防御性校验
        var mtimeAfter = TryGetLastWriteUtc(path);
        if (mtimeBefore is not null && mtimeAfter is not null && mtimeBefore == mtimeAfter)
        {
            EnforceCacheBound();
            _metadataCache[path] = new MetadataCacheEntry(mtimeAfter.Value, metadata);
        }

        return metadata;
    }

    private void InvalidateMetadataCache(string workspaceId, string collectionId)
    {
        var path = _paths.GetItemsJsonlPath(workspaceId, collectionId);
        _metadataCache.TryRemove(path, out _);
    }

    /// <summary>P0-fix: 防止缓存无限增长；超过上限时清空（本地开发场景，简单策略）。</summary>
    private void EnforceCacheBound()
    {
        if (_metadataCache.Count >= MaxCacheEntries)
        {
            _metadataCache.Clear();
        }
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

    /// <summary>
    /// Keyset 续取过滤：仅保留排序上严格位于游标之后的条目（文本命中路径）。
    /// 文件存储无 ts_rank/importance 排序，按 (updated_at, id) 近似续取（id 为决胜键）。
    /// </summary>
    private static bool IsAfterCursor(ContextItem item, ContextQueryCursor? after)
    {
        if (after is null)
        {
            return true;
        }

        var timeCmp = item.UpdatedAt.CompareTo(after.UpdatedAt);
        if (timeCmp != 0)
        {
            return timeCmp < 0;
        }
        return string.CompareOrdinal(item.Id, after.Id) < 0;
    }

    /// <summary>Keyset 续取过滤的元数据版本（无 QueryText 路径）。</summary>
    private static bool IsAfterCursor(ContextItemMetadata metadata, ContextQueryCursor? after)
    {
        if (after is null)
        {
            return true;
        }

        var timeCmp = metadata.UpdatedAt.CompareTo(after.UpdatedAt);
        if (timeCmp != 0)
        {
            return timeCmp < 0;
        }
        return string.CompareOrdinal(metadata.Id, after.Id) < 0;
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


