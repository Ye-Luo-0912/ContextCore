using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 缓存失效边界辅助：从 Store 实体或写入参数构建 <see cref="CacheInvalidationKey"/>。
/// 仅供 <c>InvalidatingXxxStoreDecorator</c> 使用，统一失效键的 StoreKind 与字段映射。
/// 仅保留 Data Plane Store（读路径可能被缓存的 Store）的失效键。
/// </summary>
internal static class InvalidationKeys
{
    public const string ContextStore = "ContextStore";
    public const string MemoryStore = "MemoryStore";
    public const string RelationStore = "RelationStore";
    public const string ConstraintStore = "ConstraintStore";
    public const string ContextIndex = "ContextIndex";
    public const string GlobalContextStore = "GlobalContextStore";
    public const string WorkingMemoryService = "WorkingMemoryService";
    public const string VectorStore = "VectorStore";

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

    public static CacheInvalidationKey ForWorkingMemory(WorkingMemoryItem item)
        => new(WorkingMemoryService, item.WorkspaceId, item.CollectionId, item.Id);

    public static CacheInvalidationKey ForWorkingMemory(string workspaceId, string collectionId)
        => new(WorkingMemoryService, workspaceId, collectionId, EntityId: null);

    public static CacheInvalidationKey ForVector(VectorRecord record)
        => new(VectorStore, record.WorkspaceId, record.CollectionId ?? string.Empty, record.Id);

    public static CacheInvalidationKey ForVector(string workspaceId, string vectorId)
        => new(VectorStore, workspaceId, string.Empty, vectorId);
}

/// <summary>
/// 失效边界 Decorator 基类：集中 commit-point 之后的失效信号 + 版本递增，
/// 统一使用 <see cref="CancellationToken.None"/>，确保写入提交后即使原请求取消也必须完成。
/// 派生类在 _inner 写入成功后调用 <see cref="AfterCommitAsync"/>。
/// </summary>
public abstract class InvalidatingStoreDecoratorBase
{
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    protected InvalidatingStoreDecoratorBase(IStateCacheInvalidator invalidator, IContextStateVersionStore? versionStore)
    {
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    /// <summary>
    /// Commit point 之后的失效协调：失效信号 + 版本递增，均使用 <see cref="CancellationToken.None"/>。
    /// 调用方必须在 _inner 写入成功后调用此方法。
    /// </summary>
    /// <param name="key">失效范围键（其 WorkspaceId/CollectionId/StoreKind 同时用于版本递增）。</param>
    protected async Task AfterCommitAsync(CacheInvalidationKey key)
    {
        await _invalidator.InvalidateAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (_versionStore is not null)
        {
            await _versionStore.BumpVersionAsync(
                key.WorkspaceId, key.CollectionId, key.StoreKind, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 包装 <see cref="IContextStore"/>，在写入成功（SaveAsync/DeleteAsync）后触发缓存失效。
/// 失效边界 Decorator：本身不缓存，仅向 <see cref="IStateCacheInvalidator"/> 发出失效信号。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IContextStore))]
public sealed partial class InvalidatingContextStoreDecorator;

public sealed partial class InvalidatingContextStoreDecorator
{
    public async Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContext(item)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContext(workspaceId, collectionId, id)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IMemoryStore"/>，在写入成功（SaveAsync/UpdateStatusAsync）后触发缓存失效。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IMemoryStore))]
public sealed partial class InvalidatingMemoryStoreDecorator;

public sealed partial class InvalidatingMemoryStoreDecorator
{
    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForMemory(item)).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        ContextMemoryStatus status,
        CancellationToken cancellationToken = default)
    {
        await _inner.UpdateStatusAsync(workspaceId, collectionId, id, status, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForMemory(workspaceId, collectionId, id)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IRelationStore"/>，在写入成功（SaveAsync/DeleteAsync/BatchUpsertAsync）后触发缓存失效。
/// 批量写入按集合范围失效（EntityId=null），避免逐条信号放大。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IRelationStore))]
public sealed partial class InvalidatingRelationStoreDecorator;

public sealed partial class InvalidatingRelationStoreDecorator
{
    public async Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForRelation(relation)).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.DeleteAsync(workspaceId, collectionId, relationId, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForRelation(workspaceId, collectionId, relationId)).ConfigureAwait(false);
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
            await AfterCommitAsync(key).ConfigureAwait(false);
        }
    }

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
[GenerateInvalidatingDecorator(typeof(IConstraintStore))]
public sealed partial class InvalidatingConstraintStoreDecorator;

public sealed partial class InvalidatingConstraintStoreDecorator
{
    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(constraint, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForConstraint(constraint)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IContextIndex"/>，在写入成功（UpsertAsync）后触发缓存失效。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IContextIndex))]
public sealed partial class InvalidatingContextIndexDecorator;

public sealed partial class InvalidatingContextIndexDecorator
{
    public async Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContextIndex(entry)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IGlobalContextStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IGlobalContextStore))]
public sealed partial class InvalidatingGlobalContextStoreDecorator;

public sealed partial class InvalidatingGlobalContextStoreDecorator
{
    public async Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForGlobal(item)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IWorkingMemoryService"/>，在写入成功（AddAsync/ClearAsync/SetActiveContextAsync/SetCurrentTaskAsync）后触发缓存失效。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IWorkingMemoryService))]
public sealed partial class InvalidatingWorkingMemoryServiceDecorator;

public sealed partial class InvalidatingWorkingMemoryServiceDecorator
{
    public async Task<WorkingMemoryItem> AddAsync(WorkingMemoryItem item, CancellationToken cancellationToken = default)
    {
        var result = await _inner.AddAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result)).ConfigureAwait(false);
        return result;
    }

    public async Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await _inner.ClearAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(workspaceId, collectionId)).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetActiveContextAsync(activeContext, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId)).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetCurrentTaskAsync(currentTask, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId)).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// 包装 <see cref="IVectorStore"/>，在写入成功（UpsertAsync/DeleteAsync）后触发缓存失效。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IVectorStore))]
public sealed partial class InvalidatingVectorStoreDecorator;

public sealed partial class InvalidatingVectorStoreDecorator
{
    public async Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVector(record)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(workspaceId, vectorId, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVector(workspaceId, vectorId)).ConfigureAwait(false);
    }
}
