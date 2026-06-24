using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>
/// 基于文件系统的记忆存储，同时实现 <see cref="IMemoryStore"/>、
/// <see cref="IWorkingMemoryService"/>、<see cref="IPromotionRecordStore"/> 和 <see cref="IPromotionCandidateStore"/>。
/// 工作记忆、稳定记忆及晋升记录均持久化为 JSON/JSONL 文件。
/// </summary>
public sealed class FileMemoryStore : IMemoryStore, IWorkingMemoryService, IPromotionRecordStore, IPromotionCandidateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileFormatSerializer _serializer;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;

    public FileMemoryStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileMemoryStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _serializer = serializer;
        _reader = new FileSystemReader();
        _writer = new FileSystemWriter();
        _jsonLines = new FileJsonLineStore(serializer, _reader, _writer);
    }

    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var normalized = Normalize(item);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in GetWritableMemoryPaths(normalized.WorkspaceId, normalized.CollectionId))
            {
                var existing = await _jsonLines.ReadAsync<ContextMemoryItem>(path, cancellationToken)
                    .ConfigureAwait(false);

                var updated = existing
                    .Where(value => !string.Equals(value.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
            }

            var targetPath = GetMemoryPath(normalized.WorkspaceId, normalized.CollectionId, normalized.Layer);
            var target = await _jsonLines.ReadAsync<ContextMemoryItem>(targetPath, cancellationToken)
                .ConfigureAwait(false);

            var targetUpdated = target
                .Where(value => !string.Equals(value.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(value => value.UpdatedAt)
                .ToArray();

            await _jsonLines.WriteAsync(targetPath, targetUpdated, cancellationToken).ConfigureAwait(false);

            if (normalized.Layer == ContextMemoryLayer.Working)
            {
                await EnsureActiveContextFileAsync(
                    normalized.WorkspaceId,
                    normalized.CollectionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextMemoryItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in GetMemoryPaths(workspaceId, collectionId))
            {
                var items = await _jsonLines.ReadAsync<ContextMemoryItem>(path, cancellationToken)
                    .ConfigureAwait(false);
                var match = items.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return Clone(match);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ContextMemoryItem>();
            var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);

            foreach (var collectionId in collectionIds)
            {
                foreach (var path in GetMemoryPaths(query.WorkspaceId, collectionId, query.Layer))
                {
                    var items = await _jsonLines.ReadAsync<ContextMemoryItem>(path, cancellationToken)
                        .ConfigureAwait(false);

                    results.AddRange(items.Where(item => Matches(item, query)));
                }
            }

            var skip = Math.Max(0, query.Skip);
            var take = query.Take > 0 ? query.Take : 50;

            return
            [
                .. results
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(item => item.UpdatedAt)
                        .First())
                    .OrderByDescending(item => item.Importance)
                    .ThenByDescending(item => item.UpdatedAt)
                    .Skip(skip)
                    .Take(take)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        ContextMemoryStatus status,
        CancellationToken cancellationToken = default)
    {
        var item = await GetAsync(workspaceId, collectionId, id, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return;
        }

        await SaveAsync(new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            SourceRefs = [.. item.SourceRefs],
            RelationRefs = [.. item.RelationRefs],
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version + 1,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextMemoryItem> AddAsync(
        ContextMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var workingItem = Normalize(new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            SourceRefs = [.. item.SourceRefs],
            RelationRefs = [.. item.RelationRefs],
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        });

        await SaveAsync(workingItem, cancellationToken).ConfigureAwait(false);

        return workingItem;
    }

    public async Task<WorkingMemoryItem> AddAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var workingItem = Normalize(item);

        await SaveAsync(ToMemoryItem(workingItem), cancellationToken).ConfigureAwait(false);

        return workingItem;
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var recent = await QueryAsync(
            new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = ContextMemoryLayer.Working,
                Take = take
            },
            cancellationToken).ConfigureAwait(false);

        return recent.Select(FromMemoryItem).ToArray();
    }

    public async Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.WriteAsync(
                _paths.GetRecentMemoryJsonlPath(workspaceId, collectionId),
                Array.Empty<ContextMemoryItem>(),
                cancellationToken).ConfigureAwait(false);
            await _jsonLines.WriteAsync(
                _paths.GetLegacyRecentMemoryJsonlPath(workspaceId, collectionId),
                Array.Empty<ContextMemoryItem>(),
                cancellationToken).ConfigureAwait(false);
            await WriteSingleJsonAsync(
                _paths.GetActiveContextJsonPath(workspaceId, collectionId),
                new WorkingMemoryActiveContext(),
                cancellationToken).ConfigureAwait(false);
            await WriteSingleJsonAsync(
                _paths.GetCurrentTaskJsonPath(workspaceId, collectionId),
                new WorkingMemoryCurrentTask(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeContext = await ReadSingleJsonAsync<WorkingMemoryActiveContext>(
                _paths.GetActiveContextJsonPath(workspaceId, collectionId),
                cancellationToken).ConfigureAwait(false)
                ?? await ReadSingleJsonAsync<WorkingMemoryActiveContext>(
                    _paths.GetLegacyActiveContextJsonPath(workspaceId, collectionId),
                    cancellationToken).ConfigureAwait(false);

            if (activeContext is null || IsEmpty(activeContext))
            {
                return null;
            }

            return Clone(Normalize(activeContext));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);

        var normalized = Normalize(activeContext);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteSingleJsonAsync(
                _paths.GetActiveContextJsonPath(normalized.WorkspaceId, normalized.CollectionId),
                normalized,
                cancellationToken).ConfigureAwait(false);

            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentTask = await ReadSingleJsonAsync<WorkingMemoryCurrentTask>(
                _paths.GetCurrentTaskJsonPath(workspaceId, collectionId),
                cancellationToken).ConfigureAwait(false);

            if (currentTask is null || IsEmpty(currentTask))
            {
                return null;
            }

            return Clone(Normalize(currentTask));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);

        var normalized = Normalize(currentTask);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteSingleJsonAsync(
                _paths.GetCurrentTaskJsonPath(normalized.WorkspaceId, normalized.CollectionId),
                normalized,
                cancellationToken).ConfigureAwait(false);

            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePromotionRecordAsync(
        ContextPromotionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = _paths.GetPromotionLogJsonlPath(record.WorkspaceId, record.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _jsonLines.ReadAsync<ContextPromotionRecord>(path, cancellationToken)
                .ConfigureAwait(false);
            var updated = existing.Append(record).ToArray();

            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextPromotionRecord>> QueryPromotionRecordsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = new List<ContextPromotionRecord>();
            records.AddRange(await _jsonLines.ReadAsync<ContextPromotionRecord>(
                _paths.GetPromotionLogJsonlPath(workspaceId, collectionId),
                cancellationToken).ConfigureAwait(false));
            records.AddRange(await _jsonLines.ReadAsync<ContextPromotionRecord>(
                _paths.GetLegacyPromotionLogJsonlPath(workspaceId, collectionId),
                cancellationToken).ConfigureAwait(false));

            var count = take > 0 ? take : 50;
            return records
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.CreatedAt).First())
                .OrderByDescending(item => item.CreatedAt)
                .Take(count)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePromotionCandidateAsync(
        PromotionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var normalized = Normalize(candidate);
        var path = _paths.GetPromotionCandidatesJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _jsonLines.ReadAsync<PromotionCandidate>(path, cancellationToken)
                .ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(item => item.UpdatedAt)
                .ToArray();

            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetPromotionCandidatesJsonlPath(workspaceId, collectionId);
            var candidates = await _jsonLines.ReadAsync<PromotionCandidate>(path, cancellationToken)
                .ConfigureAwait(false);

            var candidate = candidates.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

            return candidate is null ? null : Clone(candidate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PromotionCandidate>> QueryPromotionCandidatesAsync(
        string workspaceId,
        string collectionId,
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetPromotionCandidatesJsonlPath(workspaceId, collectionId);
            var candidates = await _jsonLines.ReadAsync<PromotionCandidate>(path, cancellationToken)
                .ConfigureAwait(false);

            var count = take > 0 ? take : 50;
            return candidates
                .Where(item => status is null || item.Status == status.Value)
                .OrderByDescending(item => item.UpdatedAt)
                .Take(count)
                .Select(item => Clone(item))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetPromotionCandidatesJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _jsonLines.ReadAsync<PromotionCandidate>(path, cancellationToken)
                .ConfigureAwait(false);
            var current = existing.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            var updatedCandidate = Clone(
                current,
                status: status,
                reviewer: reviewer,
                reason: reason,
                updatedAt: DateTimeOffset.UtcNow);
            var updated = existing
                .Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                .Append(updatedCandidate)
                .OrderByDescending(item => item.UpdatedAt)
                .ToArray();

            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);

            return Clone(updatedCandidate);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureActiveContextFileAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var activeContextPath = _paths.GetActiveContextJsonPath(workspaceId, collectionId);
        if (File.Exists(activeContextPath))
        {
            return;
        }

        await WriteSingleJsonAsync(activeContextPath, new WorkingMemoryActiveContext(), cancellationToken)
            .ConfigureAwait(false);
    }

    private string GetMemoryPath(
        string workspaceId,
        string collectionId,
        ContextMemoryLayer layer)
    {
        return layer switch
        {
            ContextMemoryLayer.Working => _paths.GetRecentMemoryJsonlPath(workspaceId, collectionId),
            ContextMemoryLayer.Stable => _paths.GetStableMemoryJsonlPath(workspaceId, collectionId),
            _ => _paths.GetMemoryCandidatesJsonlPath(workspaceId, collectionId)
        };
    }

    private IReadOnlyList<string> GetMemoryPaths(
        string workspaceId,
        string collectionId,
        ContextMemoryLayer? layer = null)
    {
        if (layer is not null)
        {
            return layer.Value == ContextMemoryLayer.Working
                ? new[]
                {
                    _paths.GetRecentMemoryJsonlPath(workspaceId, collectionId),
                    _paths.GetLegacyRecentMemoryJsonlPath(workspaceId, collectionId)
                }
                : layer.Value == ContextMemoryLayer.Stable
                    ? new[]
                    {
                        _paths.GetStableMemoryJsonlPath(workspaceId, collectionId),
                        _paths.GetLegacyStableMemoryJsonlPath(workspaceId, collectionId)
                    }
                    : new[] { GetMemoryPath(workspaceId, collectionId, layer.Value) };
        }

        return
        [
            _paths.GetRecentMemoryJsonlPath(workspaceId, collectionId),
            _paths.GetLegacyRecentMemoryJsonlPath(workspaceId, collectionId),
            _paths.GetMemoryCandidatesJsonlPath(workspaceId, collectionId),
            _paths.GetStableMemoryJsonlPath(workspaceId, collectionId),
            _paths.GetLegacyStableMemoryJsonlPath(workspaceId, collectionId)
        ];
    }

    private IReadOnlyList<string> GetWritableMemoryPaths(string workspaceId, string collectionId)
        =>
        [
            _paths.GetRecentMemoryJsonlPath(workspaceId, collectionId),
            _paths.GetMemoryCandidatesJsonlPath(workspaceId, collectionId),
            _paths.GetStableMemoryJsonlPath(workspaceId, collectionId)
        ];

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

        return Directory.EnumerateDirectories(collectionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();
    }

    private static bool Matches(ContextMemoryItem item, ContextMemoryQuery query)
    {
        if (query.Layer is not null && item.Layer != query.Layer)
        {
            return false;
        }

        if (query.Status is not null && item.Status != query.Status)
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

        if (query.SourceRefs.Count > 0)
        {
            var sourceRefs = item.SourceRefs
                .Append(item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!query.SourceRefs.Any(sourceRefs.Contains))
            {
                return false;
            }
        }

        return true;
    }

    private static ContextMemoryItem Normalize(ContextMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static WorkingMemoryItem Normalize(WorkingMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static WorkingMemoryActiveContext Normalize(WorkingMemoryActiveContext item)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = string.IsNullOrWhiteSpace(item.CurrentTaskId) ? null : item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ContextRefs = item.ContextRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt
        };
    }

    private static WorkingMemoryCurrentTask Normalize(WorkingMemoryCurrentTask item)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryCurrentTask
        {
            TaskId = string.IsNullOrWhiteSpace(item.TaskId) ? Guid.NewGuid().ToString("N") : item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static PromotionCandidate Normalize(PromotionCandidate item)
    {
        var now = DateTimeOffset.UtcNow;

        return new PromotionCandidate
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static ContextMemoryItem Clone(ContextMemoryItem item)
    {
        return new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static WorkingMemoryItem FromMemoryItem(ContextMemoryItem item)
    {
        return new WorkingMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static ContextMemoryItem ToMemoryItem(WorkingMemoryItem item)
    {
        return new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Status = ContextMemoryStatus.Candidate,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = 1,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static WorkingMemoryActiveContext Clone(WorkingMemoryActiveContext item)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs.ToArray(),
            ContextRefs = item.ContextRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = item.UpdatedAt
        };
    }

    private static WorkingMemoryCurrentTask Clone(WorkingMemoryCurrentTask item)
    {
        return new WorkingMemoryCurrentTask
        {
            TaskId = item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static PromotionCandidate Clone(
        PromotionCandidate item,
        PromotionCandidateStatus? status = null,
        string? reviewer = null,
        string? reason = null,
        DateTimeOffset? updatedAt = null)
    {
        return new PromotionCandidate
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = status ?? item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = reason ?? item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = reviewer ?? item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = updatedAt ?? item.UpdatedAt
        };
    }

    private async Task WriteSingleJsonAsync<T>(
        string path,
        T item,
        CancellationToken cancellationToken)
        where T : class
    {
        await _writer.WriteAllTextAtomicAsync(
            path,
            _serializer.Serialize(item),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> ReadSingleJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
        where T : class
    {
        var json = await _reader.ReadAllTextAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return _serializer.Deserialize<T>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool IsEmpty(WorkingMemoryActiveContext item)
    {
        return string.IsNullOrWhiteSpace(item.WorkspaceId)
            && string.IsNullOrWhiteSpace(item.CollectionId)
            && string.IsNullOrWhiteSpace(item.CurrentTaskId)
            && string.IsNullOrWhiteSpace(item.Summary)
            && item.MemoryRefs.Count == 0
            && item.ContextRefs.Count == 0
            && item.Metadata.Count == 0
            && item.UpdatedAt == default;
    }

    private static bool IsEmpty(WorkingMemoryCurrentTask item)
    {
        return string.IsNullOrWhiteSpace(item.TaskId)
            && string.IsNullOrWhiteSpace(item.WorkspaceId)
            && string.IsNullOrWhiteSpace(item.CollectionId)
            && string.IsNullOrWhiteSpace(item.Title)
            && string.IsNullOrWhiteSpace(item.Description)
            && string.IsNullOrWhiteSpace(item.Status)
            && item.Tags.Count == 0
            && item.Metadata.Count == 0
            && item.CreatedAt == default
            && item.UpdatedAt == default;
    }
}
