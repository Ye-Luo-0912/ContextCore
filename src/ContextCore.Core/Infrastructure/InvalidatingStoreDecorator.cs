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
/// 失效边界 Decorator 基类：集中 commit-point 之后的版本递增 + 失效信号，
/// 统一使用 <see cref="CancellationToken.None"/>，确保写入提交后即使原请求取消也必须完成。
/// 派生类在 _inner 写入成功后调用 <see cref="AfterCommitAsync"/>。
/// </summary>
/// <remarks>
/// R13.0 #5: version bump 先于 physical eviction。
/// 版本递增先执行——版本号是版本感知读路径的"真相源"。bump 完成后，任何并发缓存读取
/// 即使尚未被 InvalidateAsync 物理移除，也会因版本失配而被视为 stale（GetAsync 返回 null
/// 并计 VersionMismatch）。InvalidateAsync 作为 best-effort 物理清理，回收已被版本判定为 stale 的条目，
/// 避免条目驻留至下次读取才淘汰。此顺序消除"eviction 与 bump 之间窗口"——
/// 该窗口内并发 miss 重新计算并以旧版本快照写入缓存，造成单次 stale 命中。
///
/// R14-PG-7: 多实例 cache invalidation 语义。
/// bump 通过 <see cref="IContextStateVersionStore"/> 完成，其实现决定跨实例可见性：
/// - InMemoryContextStateVersionStore（FileSystem/InMemory provider 默认）：进程内可见，
///   仅本实例 cache 感知到 bump；多实例场景下其他实例 cache 不会失配，但 FileSystem/InMemory
///   本就是单机 provider，不存在多实例需求。
/// - PostgresContextStateVersionStore（Postgres provider，R14-PG-6）：版本号持久化到 Postgres，
///   多实例共享同一行级锁原子自增的版本号；Instance A bump 后，Instance B 的 cache.GetAsync
///   通过 GetVersionsAsync 读到新版本号，触发 VersionMismatch，重新从 store 读取。
/// 物理失效（InvalidateAsync）始终进程内，跨实例 cache 仅靠版本感知 GetAsync 被动失效——
/// 不实现 LISTEN/NOTIFY 主动通知，避免引入额外复杂度。
/// </remarks>
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
    /// Commit point 之后的失效协调：版本递增 + 失效信号，均使用 <see cref="CancellationToken.None"/>。
    /// 调用方必须在 _inner 写入成功后调用此方法。
    /// </summary>
    /// <param name="key">失效范围键（其 WorkspaceId/CollectionId/StoreKind 同时用于版本递增）。</param>
    protected async Task AfterCommitAsync(CacheInvalidationKey key)
    {
        // R13.0 #5: 版本先于物理失效——版本是版本感知读路径的真相源，
        // bump 完成后并发读取即使命中未物理移除的条目也会因版本失配返回 null。
        if (_versionStore is not null)
        {
            await _versionStore.BumpVersionAsync(
                key.WorkspaceId, key.CollectionId, key.StoreKind, CancellationToken.None).ConfigureAwait(false);
        }
        // 物理失效：best-effort 清理已被版本判定为 stale 的条目，避免驻留至下次读取。
        await _invalidator.InvalidateAsync(key, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IContextStore"/>，在写入成功（SaveAsync/DeleteAsync）后触发缓存失效。
/// 失效边界 Decorator：本身不缓存，仅向 <see cref="IStateCacheInvalidator"/> 发出失效信号。
/// 同时透传 <see cref="IContextStoreBatchLookup"/> / <see cref="IContextStoreMetadataLookup"/> 能力接口，
/// 确保 Retrieval 通道能走批量查询 / 元数据投影路径。
/// P0-3：透传 <see cref="ITransactionalContextStore"/> 能力接口，让事务路径在 Decorator 包装下仍可被检测到。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IContextStore), typeof(IContextStoreBatchLookup), typeof(IContextStoreMetadataLookup), typeof(ITransactionalContextStore))]
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

    /// <summary>
    /// P0-3：事务作用域内保存条目。委托给底层 store 的事务重载，<b>不触发缓存失效</b>——
    /// 事务尚未 Commit，数据未真正落库；失效应在外层 scope.CommitAsync 成功后由调用方触发。
    /// 当前实现依赖版本失配（<see cref="InMemoryContextStateCache"/> 的 VersionMismatch 路径）
    /// 与 TTL 兜底；未来可扩展 <see cref="IWriteTransactionScope"/> 暴露 commit 回调以触发精确失效。
    /// </summary>
    /// <exception cref="InvalidOperationException">底层 store 未实现 <see cref="ITransactionalContextStore"/>。</exception>
    public async Task SaveAsync(ContextItem item, IWriteTransactionScope scope, CancellationToken cancellationToken = default)
    {
        if (_inner is not ITransactionalContextStore txStore)
        {
            throw new InvalidOperationException(
                $"底层 IContextStore '{_inner.GetType().FullName}' 未实现 ITransactionalContextStore，无法走事务路径。" +
                "请确保 Postgres provider 已正确注册，或回退到无事务路径（不注册 IWriteTransactionScopeFactory）。");
        }

        await txStore.SaveAsync(item, scope, cancellationToken).ConfigureAwait(false);
        // 故意不调用 AfterCommitAsync——事务未提交，缓存失效应等待 Commit 成功后触发。
    }
}

/// <summary>
/// 包装 <see cref="IMemoryStore"/>，在写入成功（SaveAsync/UpdateStatusAsync）后触发缓存失效。
/// 同时透传 <see cref="IMemoryStoreBatchLookup"/> / <see cref="IMemoryStoreMetadataLookup"/> 能力接口，
/// 确保 Retrieval 通道能走批量查询 / 元数据投影路径。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IMemoryStore), typeof(IMemoryStoreBatchLookup), typeof(IMemoryStoreMetadataLookup))]
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
/// P0-3：透传 <see cref="ITransactionalRelationStore"/> 能力接口，让事务路径在 Decorator 包装下仍可被检测到。
/// </summary>
[GenerateInvalidatingDecorator(typeof(IRelationStore), typeof(ITransactionalRelationStore))]
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

    /// <summary>
    /// P0-3：事务作用域内批量 upsert。委托给底层 store 的事务重载，<b>不触发缓存失效</b>——
    /// 事务尚未 Commit。失效应在外层 scope.CommitAsync 成功后由调用方触发。
    /// </summary>
    public async Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        if (_inner is not ITransactionalRelationStore txStore)
        {
            throw new InvalidOperationException(
                $"底层 IRelationStore '{_inner.GetType().FullName}' 未实现 ITransactionalRelationStore，无法走事务路径。" +
                "请确保 Postgres provider 已正确注册，或回退到无事务路径（不注册 IWriteTransactionScopeFactory）。");
        }

        // 物化一次，保证 inner 与潜在后续失效信号使用同一份列表。
        var list = relations as IReadOnlyCollection<ContextRelation> ?? relations.ToList();
        await txStore.BatchUpsertAsync(list, scope, cancellationToken).ConfigureAwait(false);
        // 故意不调用 AfterCommitAsync——事务未提交。
    }

    /// <summary>
    /// P0-3：事务作用域内删除单条关系。委托给底层 store 的事务重载，<b>不触发缓存失效</b>——
    /// 事务尚未 Commit。失效应在外层 scope.CommitAsync 成功后由调用方触发。
    /// </summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        if (_inner is not ITransactionalRelationStore txStore)
        {
            throw new InvalidOperationException(
                $"底层 IRelationStore '{_inner.GetType().FullName}' 未实现 ITransactionalRelationStore，无法走事务路径。" +
                "请确保 Postgres provider 已正确注册，或回退到无事务路径（不注册 IWriteTransactionScopeFactory）。");
        }

        var result = await txStore.DeleteAsync(workspaceId, collectionId, relationId, scope, cancellationToken).ConfigureAwait(false);
        // 故意不调用 AfterCommitAsync——事务未提交。
        return result;
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
