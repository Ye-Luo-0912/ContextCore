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

    // R11-P4：剩余 Store 的 StoreKind 常量。
    public const string ContextCollectionStore = "ContextCollectionStore";
    public const string ContextPackageBuildTraceStore = "ContextPackageBuildTraceStore";
    public const string ContextPackagePolicyStore = "ContextPackagePolicyStore";
    public const string DecisionTraceStore = "DecisionTraceStore";
    public const string StableLifecycleReviewStore = "StableLifecycleReviewStore";
    public const string CandidateConstraintReviewStore = "CandidateConstraintReviewStore";
    public const string ConstraintGapCandidateStore = "ConstraintGapCandidateStore";
    public const string PromotionRecordStore = "PromotionRecordStore";
    public const string PromotionCandidateStore = "PromotionCandidateStore";
    public const string WorkingMemoryService = "WorkingMemoryService";
    public const string RelationReviewStore = "RelationReviewStore";
    public const string VectorStore = "VectorStore";
    public const string VectorReindexReportStore = "VectorReindexReportStore";
    public const string VectorLifecycleMetadataReviewStore = "VectorLifecycleMetadataReviewStore";
    public const string VectorLifecycleSidecarMetadataStore = "VectorLifecycleSidecarMetadataStore";
    public const string VectorLifecycleMetadataReviewCandidateStore = "VectorLifecycleMetadataReviewCandidateStore";
    public const string LearningFeedbackStore = "LearningFeedbackStore";
    public const string LearningFeedbackReviewStore = "LearningFeedbackReviewStore";
    public const string ShortTermPromotionCandidateStore = "ShortTermPromotionCandidateStore";

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

    // R11-P4：剩余 Store 的失效键工厂方法。集合范围失效（EntityId=null）用于 AppendReviewAsync 等批量影响读取的方法。

    public static CacheInvalidationKey ForContextCollection(ContextCollection collection)
        => new(ContextCollectionStore, collection.WorkspaceId, collection.Id, EntityId: null);

    public static CacheInvalidationKey ForPackageBuildTrace(ContextPackageBuildResult result)
        => new(ContextPackageBuildTraceStore, result.Package.WorkspaceId, result.Package.CollectionId, result.BuildId);

    public static CacheInvalidationKey ForPackagePolicy(ContextPackagePolicy policy)
        => new(ContextPackagePolicyStore, policy.WorkspaceId, policy.CollectionId ?? string.Empty, policy.Id);

    public static CacheInvalidationKey ForDecisionTrace(ContextDecisionRecord record)
        => new(DecisionTraceStore, record.WorkspaceId, record.CollectionId, record.DecisionId);

    public static CacheInvalidationKey ForStableLifecycleReview(StableLifecycleReviewRecord record)
        => new(StableLifecycleReviewStore, record.WorkspaceId, record.CollectionId ?? string.Empty, EntityId: null);

    public static CacheInvalidationKey ForCandidateConstraintReview(CandidateConstraintReviewRecord record)
        => new(CandidateConstraintReviewStore, record.WorkspaceId, record.CollectionId ?? string.Empty, EntityId: null);

    public static CacheInvalidationKey ForConstraintGapCandidate(ConstraintGapCandidate candidate)
        => new(ConstraintGapCandidateStore, candidate.WorkspaceId, candidate.CollectionId, candidate.GapId);

    public static CacheInvalidationKey ForConstraintGapReview(ConstraintGapReviewRecord record)
        => new(ConstraintGapCandidateStore, record.WorkspaceId, record.CollectionId, EntityId: null);

    public static CacheInvalidationKey ForPromotionRecord(ContextPromotionRecord record)
        => new(PromotionRecordStore, record.WorkspaceId, record.CollectionId, record.Id);

    public static CacheInvalidationKey ForPromotionCandidate(PromotionCandidate candidate)
        => new(PromotionCandidateStore, candidate.WorkspaceId, candidate.CollectionId, candidate.Id);

    public static CacheInvalidationKey ForPromotionCandidate(string workspaceId, string collectionId, string entityId)
        => new(PromotionCandidateStore, workspaceId, collectionId, entityId);

    public static CacheInvalidationKey ForWorkingMemory(WorkingMemoryItem item)
        => new(WorkingMemoryService, item.WorkspaceId, item.CollectionId, item.Id);

    public static CacheInvalidationKey ForWorkingMemory(string workspaceId, string collectionId)
        => new(WorkingMemoryService, workspaceId, collectionId, EntityId: null);

    public static CacheInvalidationKey ForRelationReview(RelationReviewRecord record)
        => new(RelationReviewStore, record.WorkspaceId, record.CollectionId ?? string.Empty, EntityId: null);

    public static CacheInvalidationKey ForVector(VectorRecord record)
        => new(VectorStore, record.WorkspaceId, record.CollectionId ?? string.Empty, record.Id);

    public static CacheInvalidationKey ForVector(string workspaceId, string vectorId)
        => new(VectorStore, workspaceId, string.Empty, vectorId);

    public static CacheInvalidationKey ForVectorReindexReport(VectorReindexResult result)
        => new(VectorReindexReportStore, result.WorkspaceId, result.CollectionId, result.ReportId);

    public static CacheInvalidationKey ForVectorLifecycleMetadataReview(VectorLifecycleMetadataReviewRecord record)
        => new(VectorLifecycleMetadataReviewStore, record.WorkspaceId, record.CollectionId, record.CandidateId);

    public static CacheInvalidationKey ForVectorLifecycleSidecarMetadata(VectorLifecycleSidecarMetadataEntry entry)
        => new(VectorLifecycleSidecarMetadataStore, entry.WorkspaceId, entry.CollectionId, entry.ItemId);

    public static CacheInvalidationKey ForVectorLifecycleMetadataReviewCandidate(VectorLifecycleMetadataReviewCandidate candidate)
        => new(VectorLifecycleMetadataReviewCandidateStore, candidate.WorkspaceId, candidate.CollectionId, candidate.CandidateId);

    public static CacheInvalidationKey ForLearningFeedback(LearningFeedbackEvent feedbackEvent)
        => new(LearningFeedbackStore, feedbackEvent.WorkspaceId, feedbackEvent.CollectionId, feedbackEvent.FeedbackId);

    public static CacheInvalidationKey ForLearningFeedbackReview(LearningFeedbackReviewRecord review)
        => new(LearningFeedbackReviewStore, string.Empty, string.Empty, review.FeedbackId);

    public static CacheInvalidationKey ForShortTermPromotionCandidate(ShortTermPromotionCandidate candidate)
        => new(ShortTermPromotionCandidateStore, candidate.WorkspaceId, candidate.CollectionId, candidate.CandidateId);

    public static CacheInvalidationKey ForShortTermPromotionCandidateReview(PromotionCandidateReviewRecord record)
        => new(ShortTermPromotionCandidateStore, record.WorkspaceId, record.CollectionId, EntityId: null);

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

// R11-P4：剩余 Store 的失效边界 Decorator。

/// <summary>
/// 包装 <see cref="IContextCollectionStore"/>，在写入成功（SaveCollectionAsync）后触发集合元数据失效。
/// </summary>
public sealed class InvalidatingContextCollectionStoreDecorator : IContextCollectionStore
{
    private readonly IContextCollectionStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingContextCollectionStoreDecorator(
        IContextCollectionStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveCollectionAsync(ContextCollection collection, CancellationToken cancellationToken = default)
    {
        await _inner.SaveCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForContextCollection(collection), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, collection.WorkspaceId, collection.Id, InvalidationKeys.ContextCollectionStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
        => _inner.GetCollectionAsync(workspaceId, collectionId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IContextPackageBuildTraceStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingContextPackageBuildTraceStoreDecorator : IContextPackageBuildTraceStore
{
    private readonly IContextPackageBuildTraceStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingContextPackageBuildTraceStoreDecorator(
        IContextPackageBuildTraceStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(result, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForPackageBuildTrace(result), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.Package.WorkspaceId, result.Package.CollectionId, InvalidationKeys.ContextPackageBuildTraceStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IContextPackagePolicyStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingContextPackagePolicyStoreDecorator : IContextPackagePolicyStore
{
    private readonly IContextPackagePolicyStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingContextPackagePolicyStoreDecorator(
        IContextPackagePolicyStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextPackagePolicy policy, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForPackagePolicy(policy), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, policy.WorkspaceId, policy.CollectionId ?? string.Empty, InvalidationKeys.ContextPackagePolicyStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContextPackagePolicy?> GetAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(workspaceId, collectionId, policyId, cancellationToken);

    public Task<IReadOnlyList<ContextPackagePolicy>> QueryAsync(
        ContextPackagePolicyQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IDecisionTraceStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingDecisionTraceStoreDecorator : IDecisionTraceStore
{
    private readonly IDecisionTraceStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingDecisionTraceStoreDecorator(
        IDecisionTraceStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForDecisionTrace(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId, InvalidationKeys.DecisionTraceStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IStableLifecycleReviewStore"/>，在写入成功（AppendReviewAsync）后触发集合范围失效。
/// </summary>
public sealed class InvalidatingStableLifecycleReviewStoreDecorator : IStableLifecycleReviewStore
{
    private readonly IStableLifecycleReviewStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingStableLifecycleReviewStoreDecorator(
        IStableLifecycleReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task AppendReviewAsync(StableLifecycleReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForStableLifecycleReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId ?? string.Empty, InvalidationKeys.StableLifecycleReviewStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(stableItemId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="ICandidateConstraintReviewStore"/>，在写入成功（AppendReviewAsync）后触发集合范围失效。
/// </summary>
public sealed class InvalidatingCandidateConstraintReviewStoreDecorator : ICandidateConstraintReviewStore
{
    private readonly ICandidateConstraintReviewStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingCandidateConstraintReviewStoreDecorator(
        ICandidateConstraintReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task AppendReviewAsync(CandidateConstraintReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForCandidateConstraintReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId ?? string.Empty, InvalidationKeys.CandidateConstraintReviewStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(constraintId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IConstraintGapCandidateStore"/>，在写入成功（SaveAsync/UpdateStatusAsync/AppendReviewAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingConstraintGapCandidateStoreDecorator : IConstraintGapCandidateStore
{
    private readonly IConstraintGapCandidateStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingConstraintGapCandidateStoreDecorator(
        IConstraintGapCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task<ConstraintGapCandidate> SaveAsync(ConstraintGapCandidate candidate, CancellationToken cancellationToken = default)
    {
        var result = await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForConstraintGapCandidate(result), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.ConstraintGapCandidateStore, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(gapId, cancellationToken);

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public async Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.UpdateStatusAsync(gapId, status, reviewer, reason, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            await _invalidator.InvalidateAsync(InvalidationKeys.ForConstraintGapCandidate(result), cancellationToken).ConfigureAwait(false);
            await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.ConstraintGapCandidateStore, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public async Task AppendReviewAsync(ConstraintGapReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForConstraintGapReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId, InvalidationKeys.ConstraintGapCandidateStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(gapId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IPromotionRecordStore"/>，在写入成功（SavePromotionRecordAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingPromotionRecordStoreDecorator : IPromotionRecordStore
{
    private readonly IPromotionRecordStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingPromotionRecordStoreDecorator(
        IPromotionRecordStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SavePromotionRecordAsync(ContextPromotionRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SavePromotionRecordAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForPromotionRecord(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId, InvalidationKeys.PromotionRecordStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextPromotionRecord>> QueryPromotionRecordsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryPromotionRecordsAsync(workspaceId, collectionId, take, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IPromotionCandidateStore"/>，在写入成功（SavePromotionCandidateAsync/UpdatePromotionCandidateStatusAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingPromotionCandidateStoreDecorator : IPromotionCandidateStore
{
    private readonly IPromotionCandidateStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingPromotionCandidateStoreDecorator(
        IPromotionCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SavePromotionCandidateAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SavePromotionCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForPromotionCandidate(candidate), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, candidate.WorkspaceId, candidate.CollectionId, InvalidationKeys.PromotionCandidateStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
        => _inner.GetPromotionCandidateAsync(workspaceId, collectionId, id, cancellationToken);

    public Task<IReadOnlyList<PromotionCandidate>> QueryPromotionCandidatesAsync(
        string workspaceId,
        string collectionId,
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryPromotionCandidatesAsync(workspaceId, collectionId, status, take, cancellationToken);

    public async Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.UpdatePromotionCandidateStatusAsync(workspaceId, collectionId, id, status, reviewer, reason, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForPromotionCandidate(workspaceId, collectionId, id), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, collectionId, InvalidationKeys.PromotionCandidateStore, cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// 包装 <see cref="IWorkingMemoryService"/>，在写入成功（AddAsync/ClearAsync/SetActiveContextAsync/SetCurrentTaskAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingWorkingMemoryServiceDecorator : IWorkingMemoryService
{
    private readonly IWorkingMemoryService _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingWorkingMemoryServiceDecorator(
        IWorkingMemoryService inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task<WorkingMemoryItem> AddAsync(WorkingMemoryItem item, CancellationToken cancellationToken = default)
    {
        var result = await _inner.AddAsync(item, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForWorkingMemory(result), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.WorkingMemoryService, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.GetRecentAsync(workspaceId, collectionId, take, cancellationToken);

    public async Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await _inner.ClearAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForWorkingMemory(workspaceId, collectionId), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, collectionId, InvalidationKeys.WorkingMemoryService, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
        => _inner.GetActiveContextAsync(workspaceId, collectionId, cancellationToken);

    public async Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetActiveContextAsync(activeContext, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.WorkingMemoryService, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
        => _inner.GetCurrentTaskAsync(workspaceId, collectionId, cancellationToken);

    public async Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetCurrentTaskAsync(currentTask, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.WorkingMemoryService, cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// 包装 <see cref="IRelationReviewStore"/>，在写入成功（AppendReviewAsync）后触发集合范围失效。
/// </summary>
public sealed class InvalidatingRelationReviewStoreDecorator : IRelationReviewStore
{
    private readonly IRelationReviewStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingRelationReviewStoreDecorator(
        IRelationReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task AppendReviewAsync(RelationReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForRelationReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId ?? string.Empty, InvalidationKeys.RelationReviewStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(relationId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IVectorStore"/>，在写入成功（UpsertAsync/DeleteAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorStoreDecorator : IVectorStore
{
    private readonly IVectorStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingVectorStoreDecorator(
        IVectorStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForVector(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId ?? string.Empty, InvalidationKeys.VectorStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<VectorRecord?> GetAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(workspaceId, vectorId, cancellationToken);

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public async Task DeleteAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(workspaceId, vectorId, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(
            InvalidationKeys.ForVector(workspaceId, vectorId), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, workspaceId, string.Empty, InvalidationKeys.VectorStore, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IVectorReindexReportStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorReindexReportStoreDecorator : IVectorReindexReportStore
{
    private readonly IVectorReindexReportStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingVectorReindexReportStoreDecorator(
        IVectorReindexReportStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(VectorReindexResult result, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(result, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForVectorReindexReport(result), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, result.WorkspaceId, result.CollectionId, InvalidationKeys.VectorReindexReportStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(workspaceId, collectionId, take, cancellationToken);

    public Task<VectorReindexResult?> GetAsync(
        string reportId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(reportId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IVectorLifecycleMetadataReviewStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorLifecycleMetadataReviewStoreDecorator : IVectorLifecycleMetadataReviewStore
{
    private readonly IVectorLifecycleMetadataReviewStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingVectorLifecycleMetadataReviewStoreDecorator(
        IVectorLifecycleMetadataReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForVectorLifecycleMetadataReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId, InvalidationKeys.VectorLifecycleMetadataReviewStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _inner.ListAsync(candidateId, cancellationToken);

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(workspaceId, collectionId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IVectorLifecycleSidecarMetadataStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorLifecycleSidecarMetadataStoreDecorator : IVectorLifecycleSidecarMetadataStore
{
    private readonly IVectorLifecycleSidecarMetadataStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingVectorLifecycleSidecarMetadataStoreDecorator(
        IVectorLifecycleSidecarMetadataStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(VectorLifecycleSidecarMetadataEntry entry, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForVectorLifecycleSidecarMetadata(entry), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, entry.WorkspaceId, entry.CollectionId, InvalidationKeys.VectorLifecycleSidecarMetadataStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(workspaceId, collectionId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IVectorLifecycleMetadataReviewCandidateStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator : IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly IVectorLifecycleMetadataReviewCandidateStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator(
        IVectorLifecycleMetadataReviewCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForVectorLifecycleMetadataReviewCandidate(candidate), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, candidate.WorkspaceId, candidate.CollectionId, InvalidationKeys.VectorLifecycleMetadataReviewCandidateStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(candidateId, cancellationToken);

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="ILearningFeedbackStore"/>，在写入成功（UpsertAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingLearningFeedbackStoreDecorator : ILearningFeedbackStore
{
    private readonly ILearningFeedbackStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingLearningFeedbackStoreDecorator(
        ILearningFeedbackStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(feedbackId, cancellationToken);

    public async Task UpsertAsync(LearningFeedbackEvent feedbackEvent, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(feedbackEvent, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForLearningFeedback(feedbackEvent), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, feedbackEvent.WorkspaceId, feedbackEvent.CollectionId, InvalidationKeys.LearningFeedbackStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="ILearningFeedbackReviewStore"/>，在写入成功（UpsertAsync）后触发缓存失效。
/// 审核记录不携带 workspace/collection，按全局范围（空串）失效。
/// </summary>
public sealed class InvalidatingLearningFeedbackReviewStoreDecorator : ILearningFeedbackReviewStore
{
    private readonly ILearningFeedbackReviewStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingLearningFeedbackReviewStoreDecorator(
        ILearningFeedbackReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task UpsertAsync(LearningFeedbackReviewRecord review, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(review, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForLearningFeedbackReview(review), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, string.Empty, string.Empty, InvalidationKeys.LearningFeedbackReviewStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IShortTermPromotionCandidateStore"/>，在写入成功（SaveAsync/AppendReviewAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingShortTermPromotionCandidateStoreDecorator : IShortTermPromotionCandidateStore
{
    private readonly IShortTermPromotionCandidateStore _inner;
    private readonly IStateCacheInvalidator _invalidator;
    private readonly IContextStateVersionStore? _versionStore;

    public InvalidatingShortTermPromotionCandidateStoreDecorator(
        IShortTermPromotionCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
    {
        _inner = inner;
        _invalidator = invalidator;
        _versionStore = versionStore;
    }

    public async Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForShortTermPromotionCandidate(candidate), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, candidate.WorkspaceId, candidate.CollectionId, InvalidationKeys.ShortTermPromotionCandidateStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<ShortTermPromotionCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(candidateId, cancellationToken);

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);

    public async Task AppendReviewAsync(PromotionCandidateReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await _invalidator.InvalidateAsync(InvalidationKeys.ForShortTermPromotionCandidateReview(record), cancellationToken).ConfigureAwait(false);
        await InvalidationKeys.BumpVersionAsync(_versionStore, record.WorkspaceId, record.CollectionId, InvalidationKeys.ShortTermPromotionCandidateStore, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(candidateId, cancellationToken);
}
