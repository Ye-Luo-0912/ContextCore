using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// R10-2 缓存失效边界辅助：从 Store 实体或写入参数构建 <see cref="CacheInvalidationKey"/>。
/// 仅供 <c>InvalidatingXxxStoreDecorator</c> 使用，统一失效键的 StoreKind 与字段映射。
/// </summary>
internal static class InvalidationKeys
{
    public const string ContextStore = "ContextStore";
    public const string MemoryStore = "MemoryStore";
    public const string RelationStore = "RelationStore";
    public const string ConstraintStore = "ConstraintStore";
    public const string ContextIndex = "ContextIndex";
    public const string GlobalContextStore = "GlobalContextStore";

    public static CacheInvalidationKey ForContext(ContextItem item)
        => new(ContextStore, item.WorkspaceId, item.CollectionId, item.Id);

    public static CacheInvalidationKey ForContext(string workspaceId, string collectionId, string entityId)
        => new(ContextStore, workspaceId, collectionId, entityId);

    public static CacheInvalidationKey ForMemory(ContextMemoryItem item)
        => new(MemoryStore, item.WorkspaceId, item.CollectionId, item.Id);

    public static CacheInvalidationKey ForMemory(string workspaceId, string collectionId, string entityId)
        => new(MemoryStore, workspaceId, collectionId, entityId);

    public static CacheInvalidationKey ForRelation(ContextRelation relation)
        => new(RelationStore, relation.WorkspaceId, relation.CollectionId, relation.Id);

    public static CacheInvalidationKey ForRelation(string workspaceId, string collectionId, string entityId)
        => new(RelationStore, workspaceId, collectionId, entityId);

    public static CacheInvalidationKey ForConstraint(ContextConstraint constraint)
        => new(ConstraintStore, constraint.WorkspaceId, constraint.CollectionId ?? string.Empty, constraint.Id);

    public static CacheInvalidationKey ForContextIndex(ContextIndexEntry entry)
        => new(ContextIndex, entry.WorkspaceId, entry.CollectionId, entry.Id);

    public static CacheInvalidationKey ForGlobal(ContextGlobalItem item)
        => new(GlobalContextStore, item.WorkspaceId, item.CollectionId ?? string.Empty, item.Id);

    /// <summary>
    /// 若版本存储非空，则 bump 指定范围版本号。R10-2 P3：Decorator 在失效信号后调用，
    /// 未来 ContextStateCache 据版本号判断是否命中。版本存储未注册时为空操作。
    /// </summary>
    internal static async Task BumpVersionAsync(
        IContextStateVersionStore? versionStore,
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken)
    {
        if (versionStore is not null)
        {
            await versionStore.BumpVersionAsync(workspaceId, collectionId, storeKind, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 包装 <see cref="IContextStore"/>，在写入成功（SaveAsync/DeleteAsync）后触发缓存失效。
/// 失效边界 Decorator：本身不缓存，仅向 <see cref="IStateCacheInvalidator"/> 发出失效信号。
/// </summary>
public sealed class InvalidatingContextStoreDecorator : IContextStore
{
    private readonly IContextStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingContextStoreDecorator(
        IContextStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForContext(item), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, item.WorkspaceId, item.CollectionId, InvalidationKeys.ContextStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForContext(workspaceId, collectionId, id), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, collectionId, InvalidationKeys.ContextStore, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IMemoryStore"/>，在写入成功（SaveAsync/UpdateStatusAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingMemoryStoreDecorator : IMemoryStore
{
    private readonly IMemoryStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingMemoryStoreDecorator(
        IMemoryStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForMemory(item), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, item.WorkspaceId, item.CollectionId, InvalidationKeys.MemoryStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextMemoryItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

    public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public async Task UpdateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        ContextMemoryStatus status,
        CancellationToken cancellationToken = default)
    {
        await _inner.UpdateStatusAsync(workspaceId, collectionId, id, status, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForMemory(workspaceId, collectionId, id), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, collectionId, InvalidationKeys.MemoryStore, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IRelationStore"/>，在写入成功（SaveAsync/DeleteAsync/BatchUpsertAsync）后触发缓存失效。
/// 批量写入按集合范围失效（EntityId=null），避免逐条信号放大。
/// </summary>
public sealed class InvalidatingRelationStoreDecorator : IRelationStore
{
    private readonly IRelationStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingRelationStoreDecorator(
        IRelationStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForRelation(relation), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, relation.WorkspaceId, relation.CollectionId, InvalidationKeys.RelationStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(workspaceId, collectionId, relationId, cancellationToken);

    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.DeleteAsync(workspaceId, collectionId, relationId, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForRelation(workspaceId, collectionId, relationId), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, collectionId, InvalidationKeys.RelationStore, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        // 物化一次，避免 IEnumerable 双重枚举（inner 消费后再提取失效键会失效）。
        var list = relations as IReadOnlyCollection<ContextRelation> ?? relations.ToList();
        await _inner.BatchUpsertAsync(list, cancellationToken).ConfigureAwait(false);
        // 批量写入按集合范围失效：BatchUpsert 可能跨多实体，单集合内统一标记全集合失效更稳妥。
        foreach (var key in CollectCollectionKeys(list, InvalidationKeys.RelationStore))
        {
            await _invalidator.InvalidateAsync(key, cancellationToken).ConfigureAwait(false);
            await InvalidationKeys.BumpVersionAsync(_versionStore, key.WorkspaceId, key.CollectionId, InvalidationKeys.RelationStore, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryNeighborsAsync(query, cancellationToken);

    internal static IEnumerable<CacheInvalidationKey> CollectCollectionKeys(
        IEnumerable<ContextRelation> relations, string storeKind)
    {
        var seen = new HashSet<(string WorkspaceId, string CollectionId)>();
        foreach (var r in relations)
        {
            var tuple = (r.WorkspaceId, r.CollectionId);
            if (seen.Add(tuple))
            {
                yield return new CacheInvalidationKey(storeKind, r.WorkspaceId, r.CollectionId, EntityId: null);
            }
        }
    }
}

/// <summary>
/// 包装 <see cref="IConstraintStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingConstraintStoreDecorator : IConstraintStore
{
    private readonly IConstraintStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingConstraintStoreDecorator(
        IConstraintStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(constraint, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForConstraint(constraint), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, constraint.WorkspaceId, constraint.CollectionId ?? string.Empty, InvalidationKeys.ConstraintStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(constraintId, cancellationToken);

    public Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IContextIndex"/>，在写入成功（UpsertAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingContextIndexDecorator : IContextIndex
{
    private readonly IContextIndex _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingContextIndexDecorator(
        IContextIndex inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForContextIndex(entry), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, entry.WorkspaceId, entry.CollectionId, InvalidationKeys.ContextIndex, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IGlobalContextStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingGlobalContextStoreDecorator : IGlobalContextStore
{
    private readonly IGlobalContextStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingGlobalContextStoreDecorator(
        IGlobalContextStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForGlobal(item), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, item.WorkspaceId, item.CollectionId ?? string.Empty, InvalidationKeys.GlobalContextStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(
        ContextGlobalQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}
