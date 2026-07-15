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
public sealed class InvalidatingContextStoreDecorator : InvalidatingStoreDecoratorBase, IContextStore
{
    private readonly IContextStore _inner;

    public InvalidatingContextStoreDecorator(
        IContextStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContext(item)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForContext(workspaceId, collectionId, id)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IMemoryStore"/>，在写入成功（SaveAsync/UpdateStatusAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingMemoryStoreDecorator : InvalidatingStoreDecoratorBase, IMemoryStore
{
    private readonly IMemoryStore _inner;

    public InvalidatingMemoryStoreDecorator(
        IMemoryStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForMemory(item)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForMemory(workspaceId, collectionId, id)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IRelationStore"/>，在写入成功（SaveAsync/DeleteAsync/BatchUpsertAsync）后触发缓存失效。
/// 批量写入按集合范围失效（EntityId=null），避免逐条信号放大。
/// </summary>
public sealed class InvalidatingRelationStoreDecorator : InvalidatingStoreDecoratorBase, IRelationStore
{
    private readonly IRelationStore _inner;

    public InvalidatingRelationStoreDecorator(
        IRelationStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForRelation(relation)).ConfigureAwait(false);
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
public sealed class InvalidatingConstraintStoreDecorator : InvalidatingStoreDecoratorBase, IConstraintStore
{
    private readonly IConstraintStore _inner;

    public InvalidatingConstraintStoreDecorator(
        IConstraintStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(constraint, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForConstraint(constraint)).ConfigureAwait(false);
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
public sealed class InvalidatingContextIndexDecorator : InvalidatingStoreDecoratorBase, IContextIndex
{
    private readonly IContextIndex _inner;

    public InvalidatingContextIndexDecorator(
        IContextIndex inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContextIndex(entry)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IGlobalContextStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingGlobalContextStoreDecorator : InvalidatingStoreDecoratorBase, IGlobalContextStore
{
    private readonly IGlobalContextStore _inner;

    public InvalidatingGlobalContextStoreDecorator(
        IGlobalContextStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForGlobal(item)).ConfigureAwait(false);
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
public sealed class InvalidatingContextCollectionStoreDecorator : InvalidatingStoreDecoratorBase, IContextCollectionStore
{
    private readonly IContextCollectionStore _inner;

    public InvalidatingContextCollectionStoreDecorator(
        IContextCollectionStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveCollectionAsync(ContextCollection collection, CancellationToken cancellationToken = default)
    {
        await _inner.SaveCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForContextCollection(collection)).ConfigureAwait(false);
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
public sealed class InvalidatingContextPackageBuildTraceStoreDecorator : InvalidatingStoreDecoratorBase, IContextPackageBuildTraceStore
{
    private readonly IContextPackageBuildTraceStore _inner;

    public InvalidatingContextPackageBuildTraceStoreDecorator(
        IContextPackageBuildTraceStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(result, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForPackageBuildTrace(result)).ConfigureAwait(false);
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
public sealed class InvalidatingContextPackagePolicyStoreDecorator : InvalidatingStoreDecoratorBase, IContextPackagePolicyStore
{
    private readonly IContextPackagePolicyStore _inner;

    public InvalidatingContextPackagePolicyStoreDecorator(
        IContextPackagePolicyStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextPackagePolicy policy, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForPackagePolicy(policy)).ConfigureAwait(false);
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
public sealed class InvalidatingDecisionTraceStoreDecorator : InvalidatingStoreDecoratorBase, IDecisionTraceStore
{
    private readonly IDecisionTraceStore _inner;

    public InvalidatingDecisionTraceStoreDecorator(
        IDecisionTraceStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForDecisionTrace(record)).ConfigureAwait(false);
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
public sealed class InvalidatingStableLifecycleReviewStoreDecorator : InvalidatingStoreDecoratorBase, IStableLifecycleReviewStore
{
    private readonly IStableLifecycleReviewStore _inner;

    public InvalidatingStableLifecycleReviewStoreDecorator(
        IStableLifecycleReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task AppendReviewAsync(StableLifecycleReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForStableLifecycleReview(record)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(stableItemId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="ICandidateConstraintReviewStore"/>，在写入成功（AppendReviewAsync）后触发集合范围失效。
/// </summary>
public sealed class InvalidatingCandidateConstraintReviewStoreDecorator : InvalidatingStoreDecoratorBase, ICandidateConstraintReviewStore
{
    private readonly ICandidateConstraintReviewStore _inner;

    public InvalidatingCandidateConstraintReviewStoreDecorator(
        ICandidateConstraintReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task AppendReviewAsync(CandidateConstraintReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForCandidateConstraintReview(record)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(constraintId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IConstraintGapCandidateStore"/>，在写入成功（SaveAsync/UpdateStatusAsync/AppendReviewAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingConstraintGapCandidateStoreDecorator : InvalidatingStoreDecoratorBase, IConstraintGapCandidateStore
{
    private readonly IConstraintGapCandidateStore _inner;

    public InvalidatingConstraintGapCandidateStoreDecorator(
        IConstraintGapCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task<ConstraintGapCandidate> SaveAsync(ConstraintGapCandidate candidate, CancellationToken cancellationToken = default)
    {
        var result = await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForConstraintGapCandidate(result)).ConfigureAwait(false);
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
            await AfterCommitAsync(InvalidationKeys.ForConstraintGapCandidate(result)).ConfigureAwait(false);
        }
        return result;
    }

    public async Task AppendReviewAsync(ConstraintGapReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForConstraintGapReview(record)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(gapId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IPromotionRecordStore"/>，在写入成功（SavePromotionRecordAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingPromotionRecordStoreDecorator : InvalidatingStoreDecoratorBase, IPromotionRecordStore
{
    private readonly IPromotionRecordStore _inner;

    public InvalidatingPromotionRecordStoreDecorator(
        IPromotionRecordStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SavePromotionRecordAsync(ContextPromotionRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SavePromotionRecordAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForPromotionRecord(record)).ConfigureAwait(false);
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
public sealed class InvalidatingPromotionCandidateStoreDecorator : InvalidatingStoreDecoratorBase, IPromotionCandidateStore
{
    private readonly IPromotionCandidateStore _inner;

    public InvalidatingPromotionCandidateStoreDecorator(
        IPromotionCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SavePromotionCandidateAsync(PromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SavePromotionCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForPromotionCandidate(candidate)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForPromotionCandidate(workspaceId, collectionId, id)).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// 包装 <see cref="IWorkingMemoryService"/>，在写入成功（AddAsync/ClearAsync/SetActiveContextAsync/SetCurrentTaskAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingWorkingMemoryServiceDecorator : InvalidatingStoreDecoratorBase, IWorkingMemoryService
{
    private readonly IWorkingMemoryService _inner;

    public InvalidatingWorkingMemoryServiceDecorator(
        IWorkingMemoryService inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task<WorkingMemoryItem> AddAsync(WorkingMemoryItem item, CancellationToken cancellationToken = default)
    {
        var result = await _inner.AddAsync(item, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(workspaceId, collectionId)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForWorkingMemory(result.WorkspaceId, result.CollectionId)).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// 包装 <see cref="IRelationReviewStore"/>，在写入成功（AppendReviewAsync）后触发集合范围失效。
/// </summary>
public sealed class InvalidatingRelationReviewStoreDecorator : InvalidatingStoreDecoratorBase, IRelationReviewStore
{
    private readonly IRelationReviewStore _inner;

    public InvalidatingRelationReviewStoreDecorator(
        IRelationReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task AppendReviewAsync(RelationReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForRelationReview(record)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(relationId, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IVectorStore"/>，在写入成功（UpsertAsync/DeleteAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorStoreDecorator : InvalidatingStoreDecoratorBase, IVectorStore
{
    private readonly IVectorStore _inner;

    public InvalidatingVectorStoreDecorator(
        IVectorStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVector(record)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForVector(workspaceId, vectorId)).ConfigureAwait(false);
    }
}

/// <summary>
/// 包装 <see cref="IVectorReindexReportStore"/>，在写入成功（SaveAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingVectorReindexReportStoreDecorator : InvalidatingStoreDecoratorBase, IVectorReindexReportStore
{
    private readonly IVectorReindexReportStore _inner;

    public InvalidatingVectorReindexReportStoreDecorator(
        IVectorReindexReportStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(VectorReindexResult result, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(result, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVectorReindexReport(result)).ConfigureAwait(false);
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
public sealed class InvalidatingVectorLifecycleMetadataReviewStoreDecorator : InvalidatingStoreDecoratorBase, IVectorLifecycleMetadataReviewStore
{
    private readonly IVectorLifecycleMetadataReviewStore _inner;

    public InvalidatingVectorLifecycleMetadataReviewStoreDecorator(
        IVectorLifecycleMetadataReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVectorLifecycleMetadataReview(record)).ConfigureAwait(false);
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
public sealed class InvalidatingVectorLifecycleSidecarMetadataStoreDecorator : InvalidatingStoreDecoratorBase, IVectorLifecycleSidecarMetadataStore
{
    private readonly IVectorLifecycleSidecarMetadataStore _inner;

    public InvalidatingVectorLifecycleSidecarMetadataStoreDecorator(
        IVectorLifecycleSidecarMetadataStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(VectorLifecycleSidecarMetadataEntry entry, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVectorLifecycleSidecarMetadata(entry)).ConfigureAwait(false);
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
public sealed class InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator : InvalidatingStoreDecoratorBase, IVectorLifecycleMetadataReviewCandidateStore
{
    private readonly IVectorLifecycleMetadataReviewCandidateStore _inner;

    public InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator(
        IVectorLifecycleMetadataReviewCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForVectorLifecycleMetadataReviewCandidate(candidate)).ConfigureAwait(false);
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
public sealed class InvalidatingLearningFeedbackStoreDecorator : InvalidatingStoreDecoratorBase, ILearningFeedbackStore
{
    private readonly ILearningFeedbackStore _inner;

    public InvalidatingLearningFeedbackStoreDecorator(
        ILearningFeedbackStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(feedbackId, cancellationToken);

    public async Task UpsertAsync(LearningFeedbackEvent feedbackEvent, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(feedbackEvent, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForLearningFeedback(feedbackEvent)).ConfigureAwait(false);
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
public sealed class InvalidatingLearningFeedbackReviewStoreDecorator : InvalidatingStoreDecoratorBase, ILearningFeedbackReviewStore
{
    private readonly ILearningFeedbackReviewStore _inner;

    public InvalidatingLearningFeedbackReviewStoreDecorator(
        ILearningFeedbackReviewStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task UpsertAsync(LearningFeedbackReviewRecord review, CancellationToken cancellationToken = default)
    {
        await _inner.UpsertAsync(review, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForLearningFeedbackReview(review)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(query, cancellationToken);
}

/// <summary>
/// 包装 <see cref="IShortTermPromotionCandidateStore"/>，在写入成功（SaveAsync/AppendReviewAsync）后触发缓存失效。
/// </summary>
public sealed class InvalidatingShortTermPromotionCandidateStoreDecorator : InvalidatingStoreDecoratorBase, IShortTermPromotionCandidateStore
{
    private readonly IShortTermPromotionCandidateStore _inner;

    public InvalidatingShortTermPromotionCandidateStoreDecorator(
        IShortTermPromotionCandidateStore inner,
        IStateCacheInvalidator invalidator,
        IContextStateVersionStore? versionStore = null)
        : base(invalidator, versionStore)
    {
        _inner = inner;
    }

    public async Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _inner.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
        await AfterCommitAsync(InvalidationKeys.ForShortTermPromotionCandidate(candidate)).ConfigureAwait(false);
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
        await AfterCommitAsync(InvalidationKeys.ForShortTermPromotionCandidateReview(record)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
        => _inner.QueryReviewsAsync(candidateId, cancellationToken);
}
