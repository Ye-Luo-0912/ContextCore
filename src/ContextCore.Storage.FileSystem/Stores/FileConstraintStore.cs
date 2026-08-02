using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IConstraintStore"/> 实现，约束数据持久化为 JSONL 文件。</summary>
public sealed class FileConstraintStore : IConstraintStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    /// <summary>
    /// Provider 内按 Level/Layer 复用快照——
    /// 单次 build 内 Hard/Soft/All 三次 Query 会读同一份 global + collection JSONL，
    /// 通过 last-write-time 校验复用反序列化结果，避免 3 次重复文件 I/O。
    /// key = 文件绝对路径；value = (LastWriteTimeUtc, Items)。
    /// 写入后文件 last-write-time 改变，下次查询自然 miss → 重读最新内容，无需显式失效。
    /// </summary>
    private readonly ConcurrentDictionary<string, ConstraintFileSnapshot> _snapshots = new();

    /// <summary>测试可观察：snapshot cache 命中次数。仅用于验证 R13.2 #2 行为。</summary>
    internal int SnapshotHits;

    /// <summary>测试可观察：snapshot cache 未命中次数（触发实际文件读取）。仅用于验证 R13.2 #2 行为。</summary>
    internal int SnapshotMisses;

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

        var normalized = CompositeContextNormalizer.Normalize(constraint);
        var path = GetPath(normalized.WorkspaceId, normalized.CollectionId);

        await _jsonLines.UpsertAsync(path, normalized, item => item.Id, cancellationToken)
            .ConfigureAwait(false);

        // 写入后该路径快照必然 stale（last-write-time 已变），显式移除避免下次查询残留旧版本。
        // 即使不显式移除，下次查询也会因 last-write-time 不一致而 miss；这里只是省一次 stat。
        _snapshots.TryRemove(path, out _);
    }

    public async Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);

        foreach (var workspaceId in ResolveWorkspaceIds())
        {
            var globalItems = await ReadConstraintsWithSnapshotAsync(
                _paths.GetGlobalConstraintsJsonlPath(workspaceId),
                cancellationToken).ConfigureAwait(false);
            var globalMatch = globalItems.FirstOrDefault(item =>
                string.Equals(item.Id, constraintId, StringComparison.OrdinalIgnoreCase));
            if (globalMatch is not null)
            {
                return CompositeContextNormalizer.Normalize(globalMatch);
            }

            foreach (var collectionId in ResolveCollectionIds(workspaceId))
            {
                var items = await ReadConstraintsWithSnapshotAsync(
                    _paths.GetConstraintsJsonlPath(workspaceId, collectionId),
                    cancellationToken).ConfigureAwait(false);
                var match = items.FirstOrDefault(item =>
                    string.Equals(item.Id, constraintId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return CompositeContextNormalizer.Normalize(match);
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var constraints = new List<ContextConstraint>();
        constraints.AddRange(await ReadConstraintsWithSnapshotAsync(
            _paths.GetGlobalConstraintsJsonlPath(query.WorkspaceId),
            cancellationToken).ConfigureAwait(false));

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            constraints.AddRange(await ReadConstraintsWithSnapshotAsync(
                _paths.GetConstraintsJsonlPath(query.WorkspaceId, query.CollectionId),
                cancellationToken).ConfigureAwait(false));
        }
        else
        {
            foreach (var collectionId in ResolveCollectionIds(query.WorkspaceId))
            {
                constraints.AddRange(await ReadConstraintsWithSnapshotAsync(
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

    /// <summary>
    /// 读取约束文件并按 last-write-time 复用快照。
    /// 文件不存在时返回空列表且缓存该空结果（last-write-time = DateTime.MinValue），
    /// 避免反复 File.Exists 检查；写入后 last-write-time 改变，自动 miss。
    /// </summary>
    private async Task<IReadOnlyList<ContextConstraint>> ReadConstraintsWithSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lastWrite = QueryLastWriteTimeUtc(path);

        if (_snapshots.TryGetValue(path, out var cached)
            && cached.LastWriteTimeUtc == lastWrite)
        {
            Interlocked.Increment(ref SnapshotHits);
            return cached.Items;
        }

        Interlocked.Increment(ref SnapshotMisses);
        var items = await _jsonLines.ReadAsync<ContextConstraint>(path, cancellationToken)
            .ConfigureAwait(false);

        _snapshots[path] = new ConstraintFileSnapshot(lastWrite, items);

        return items;
    }

    private static DateTime QueryLastWriteTimeUtc(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return DateTime.MinValue;
            }
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>测试钩子：清空 snapshot cache 与计数器，保证用例间隔离。</summary>
    internal void ResetSnapshotCacheForTests()
    {
        _snapshots.Clear();
        SnapshotHits = 0;
        SnapshotMisses = 0;
    }

    private sealed record ConstraintFileSnapshot(DateTime LastWriteTimeUtc, IReadOnlyList<ContextConstraint> Items);

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

}
