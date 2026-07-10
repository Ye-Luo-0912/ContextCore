using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using System.Text;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IRelationStore"/> 实现，关系数据持久化为 JSONL 文件。</summary>
/// <remarks>
/// GRAPH-11：BatchUpsert 通过命名 Mutex 实现跨实例互斥，解决不同 FileRelationStore 实例并发读旧数据后覆盖新数据的问题。
/// 进程内仍用 SemaphoreSlim 做异步门控，避免 Mutex 长时间阻塞线程池线程。
/// </remarks>
public sealed class FileRelationStore : IRelationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileRelationStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileRelationStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    /// <summary>GRAPH-11：SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => BatchUpsertAsync([relation], cancellationToken);

    /// <summary>按关系 ID 读取单条边；供 provider parity/diagnostics 使用。</summary>
    public async Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetRelationsJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);
            return relations.FirstOrDefault(relation =>
                string.Equals(relation.Id, relationId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除单条边；供 provider parity/cleanup 使用，不参与默认业务流程。</summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetRelationsJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);
            var retained = relations
                .Where(relation => !string.Equals(relation.Id, relationId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retained.Length == relations.Count)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await _jsonLines.WriteAsync(path, retained, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量 upsert：按 (workspaceId, collectionId) 分组，每组在跨实例互斥锁内完成读改写并原子替换文件。
    /// GRAPH-11：使用命名 Mutex 解决不同 store 实例并发读旧数据后覆盖新数据的问题。
    /// </summary>
    public async Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);

        var normalized = relations.Select(Normalize).ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var group in normalized.GroupBy(r =>
                _paths.GetRelationsJsonlPath(r.WorkspaceId, r.CollectionId)))
            {
                var path = group.Key;
                // GRAPH-11：跨实例互斥 — 不同 FileRelationStore 实例共享同一文件，必须串行读改写
                using var fileLock = AcquireCrossInstanceLock(path);
                var incoming = group.ToArray();
                var incomingIds = new HashSet<string>(
                    incoming.Select(r => r.Id),
                    StringComparer.OrdinalIgnoreCase);

                var existing = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                    .ConfigureAwait(false);
                var merged = existing
                    .Where(r => !incomingIds.Contains(r.Id))
                    .Concat(incoming);

                await _jsonLines.WriteAsync(path, merged, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>GRAPH-10：统一邻居查询，在内存中过滤。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        var excludedLifecycles = query.ExcludedLifecycles.Count > 0
            ? new HashSet<string>(query.ExcludedLifecycles, StringComparer.OrdinalIgnoreCase)
            : null;
        var excludedReviewStatuses = query.ExcludedReviewStatuses.Count > 0
            ? new HashSet<string>(query.ExcludedReviewStatuses, StringComparer.OrdinalIgnoreCase)
            : null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, query.CollectionId);
            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);

            IEnumerable<ContextRelation> filtered = query.Direction switch
            {
                RelationDirection.Outgoing => relations.Where(relation =>
                    string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
                RelationDirection.Incoming => relations.Where(relation =>
                    string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
                _ => relations.Where(relation =>
                    string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
            };

            if (!string.IsNullOrWhiteSpace(query.RelationType))
            {
                filtered = filtered.Where(relation =>
                    string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase));
            }

            if (query.MinConfidence > 0)
            {
                filtered = filtered.Where(relation => relation.Confidence >= query.MinConfidence);
            }

            if (excludedLifecycles is not null)
            {
                filtered = filtered.Where(relation => !excludedLifecycles.Contains(relation.Lifecycle ?? string.Empty));
            }

            if (excludedReviewStatuses is not null)
            {
                filtered = filtered.Where(relation => !excludedReviewStatuses.Contains(relation.ReviewStatus ?? string.Empty));
            }

            return [.. filtered
                .Take(maxScan)
                .OrderByDescending(relation => relation.Weight)
                .ThenByDescending(relation => relation.Confidence)
                .ThenByDescending(relation => relation.CreatedAt)
                .Skip(effectiveSkip)
                .Take(effectiveTake)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relations = new List<ContextRelation>();
            var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);

            foreach (var collectionId in collectionIds)
            {
                var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, collectionId);
                relations.AddRange(await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            var take = query.Take > 0 ? query.Take : 50;
            var skip = query.Skip > 0 ? query.Skip : 0;

            return [.. relations
                .Where(relation => Matches(relation, query))
                .OrderByDescending(relation => relation.Weight)
                .ThenByDescending(relation => relation.Confidence)
                .ThenByDescending(relation => relation.CreatedAt)
                .Skip(skip)
                .Take(take)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// GRAPH-11：基于文件路径的命名 Mutex，提供跨进程/跨实例互斥。
    /// Mutex 名称 sanitize 为合法字符；使用 using 确保释放。
    /// </summary>
    private static Mutex AcquireCrossInstanceLock(string path)
    {
        // 将路径转为合法 Mutex 名称（去掉反斜杠、冒号等非法字符）
        var safe = new StringBuilder(path.Length);
        foreach (var ch in path)
        {
            safe.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
        }
        var mutex = new Mutex(initiallyOwned: true, name: "Global\\cc-rel-" + safe.ToString(), out var createdNew);
        if (!createdNew)
        {
            // 另一实例已持有；等待获取
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // 前一持有者异常退出；仍获得锁，继续
            }
        }
        return mutex;
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

        return [.. Directory.EnumerateDirectories(collectionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()];
    }

    private static bool Matches(ContextRelation relation, ContextRelationQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.CollectionId)
            && !string.Equals(relation.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SourceId)
            && !string.Equals(relation.SourceId, query.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId)
            && !string.Equals(relation.TargetId, query.TargetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.ItemId)
            && !string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(query.RelationType)
               || string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase);
    }

    private static ContextRelation Normalize(ContextRelation relation)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextRelation
        {
            Id = string.IsNullOrWhiteSpace(relation.Id) ? Guid.NewGuid().ToString("N") : relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = [.. relation.SourceRefs],
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt == default ? now : relation.CreatedAt,
            SourceNodeKind = relation.SourceNodeKind,
            TargetNodeKind = relation.TargetNodeKind,
            Lifecycle = relation.Lifecycle,
            ReviewStatus = relation.ReviewStatus,
            UpdatedAt = relation.UpdatedAt,
            Provenance = relation.Provenance
        };
    }
}
