using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IConstraintStore"/> 实现，约束数据持久化为 JSONL 文件。</summary>
public sealed class FileConstraintStore : IConstraintStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileConstraintStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileConstraintStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        var normalized = Normalize(constraint);
        var path = GetPath(normalized.WorkspaceId, normalized.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, item => item.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var workspaceId in ResolveWorkspaceIds())
            {
                var globalItems = await _jsonLines.ReadAsync<ContextConstraint>(
                    _paths.GetGlobalConstraintsJsonlPath(workspaceId),
                    cancellationToken).ConfigureAwait(false);
                var globalMatch = globalItems.FirstOrDefault(item =>
                    string.Equals(item.Id, constraintId, StringComparison.OrdinalIgnoreCase));
                if (globalMatch is not null)
                {
                    return Normalize(globalMatch);
                }

                foreach (var collectionId in ResolveCollectionIds(workspaceId))
                {
                    var items = await _jsonLines.ReadAsync<ContextConstraint>(
                        _paths.GetConstraintsJsonlPath(workspaceId, collectionId),
                        cancellationToken).ConfigureAwait(false);
                    var match = items.FirstOrDefault(item =>
                        string.Equals(item.Id, constraintId, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        return Normalize(match);
                    }
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var constraints = new List<ContextConstraint>();
            constraints.AddRange(await _jsonLines.ReadAsync<ContextConstraint>(
                _paths.GetGlobalConstraintsJsonlPath(query.WorkspaceId),
                cancellationToken).ConfigureAwait(false));

            if (!string.IsNullOrWhiteSpace(query.CollectionId))
            {
                constraints.AddRange(await _jsonLines.ReadAsync<ContextConstraint>(
                    _paths.GetConstraintsJsonlPath(query.WorkspaceId, query.CollectionId),
                    cancellationToken).ConfigureAwait(false));
            }
            else
            {
                foreach (var collectionId in ResolveCollectionIds(query.WorkspaceId))
                {
                    constraints.AddRange(await _jsonLines.ReadAsync<ContextConstraint>(
                        _paths.GetConstraintsJsonlPath(query.WorkspaceId, collectionId),
                        cancellationToken).ConfigureAwait(false));
                }
            }

            var take = query.Take > 0 ? query.Take : 50;

            return [.. constraints
                .Where(item => Matches(item, query))
                .OrderByDescending(item => item.Level == ConstraintLevel.Hard)
                .ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.UpdatedAt)
                .Take(take)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string workspaceId, string? collectionId)
    {
        return string.IsNullOrWhiteSpace(collectionId)
            ? _paths.GetGlobalConstraintsJsonlPath(workspaceId)
            : _paths.GetConstraintsJsonlPath(workspaceId, collectionId);
    }

    private IReadOnlyList<string> ResolveCollectionIds(string workspaceId)
    {
        var collectionsDirectory = _paths.GetCollectionsDirectory(workspaceId);
        if (!Directory.Exists(collectionsDirectory))
		{
			return [];
        }

        return [.. Directory.EnumerateDirectories(collectionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()];
    }

    private IReadOnlyList<string> ResolveWorkspaceIds()
    {
        if (!Directory.Exists(_paths.RootPath))
        {
            return Array.Empty<string>();
        }

        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        var ids = new List<string>();
        if (Directory.Exists(workspacesRoot))
        {
            ids.AddRange(Directory.EnumerateDirectories(workspacesRoot)
                .Select(Path.GetFileName)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>());
        }

        ids.AddRange(Directory.EnumerateDirectories(_paths.RootPath)
            .Where(directory => File.Exists(Path.Combine(directory, "global-constraints.jsonl")))
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>());

        return ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Matches(ContextConstraint item, ContextConstraintQuery query)
    {
        if (query.Scope is not null && item.Scope != query.Scope)
        {
            return false;
        }

        if (query.Level is not null && item.Level != query.Level)
        {
            return false;
        }

        if (query.Status is not null && item.Status != query.Status)
        {
            return false;
        }

        if (query.AppliesToRefs.Count > 0)
        {
            var appliesTo = item.AppliesToRefs
                .Concat(item.SourceRefs)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return query.AppliesToRefs.Any(appliesTo.Contains);
        }

        return true;
    }

    private static ContextConstraint Normalize(ContextConstraint constraint)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextConstraint
        {
            Id = string.IsNullOrWhiteSpace(constraint.Id) ? Guid.NewGuid().ToString("N") : constraint.Id,
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = constraint.CollectionId,
            Scope = constraint.Scope,
            Level = constraint.Level,
            Content = constraint.Content,
            AppliesToRefs = [.. constraint.AppliesToRefs],
            SourceRefs = [.. constraint.SourceRefs],
            Status = constraint.Status,
            Confidence = constraint.Confidence,
            Metadata = new Dictionary<string, string>(constraint.Metadata),
            CreatedAt = constraint.CreatedAt == default ? now : constraint.CreatedAt,
            UpdatedAt = constraint.UpdatedAt == default ? now : constraint.UpdatedAt
        };
    }
}
