using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IGlobalContextStore"/> 实现，全局上下文条目持久化为 JSONL 文件。</summary>
public sealed class FileGlobalContextStore : IGlobalContextStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileGlobalContextStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileGlobalContextStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var normalized = Normalize(item);
        var path = _paths.GetGlobalContextJsonlPath(normalized.WorkspaceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, value => value.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(
        ContextGlobalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await _jsonLines.ReadAsync<ContextGlobalItem>(
                _paths.GetGlobalContextJsonlPath(query.WorkspaceId),
                cancellationToken).ConfigureAwait(false);

            var take = query.Take > 0 ? query.Take : 50;

            return items
                .Where(item => Matches(item, query))
                .OrderByDescending(item => item.Importance)
                .ThenByDescending(item => item.UpdatedAt)
                .Take(take)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Matches(ContextGlobalItem item, ContextGlobalQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.CollectionId)
            && !string.IsNullOrWhiteSpace(item.CollectionId)
            && !string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Scope is not null && item.Scope != query.Scope)
        {
            return false;
        }

        if (query.Tags.Count > 0)
        {
            var itemTags = item.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!query.Tags.All(itemTags.Contains))
            {
                return false;
            }
        }

        if (query.Types.Count > 0
            && !query.Types.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static ContextGlobalItem Normalize(ContextGlobalItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextGlobalItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }
}
