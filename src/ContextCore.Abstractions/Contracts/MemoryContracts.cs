using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>存储和查询上下文条目之间的有向关系。</summary>
public interface IRelationStore
{
    /// <summary>保存或更新一条关系。</summary>
    Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default);

    /// <summary>批量保存或更新关系，适合压缩和打包后一次写入多个边。</summary>
    Task SaveManyAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default);

    /// <summary>按条件查询关系。</summary>
    Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定条目的所有出边和入边。</summary>
    Task<IReadOnlyList<ContextRelation>> QueryForItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定来源条目的出边。</summary>
    Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(
        string workspaceId,
        string collectionId,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>查询指向指定目标条目的入边。</summary>
    Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(
        string workspaceId,
        string collectionId,
        string targetId,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定类型的关系。</summary>
    Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(
        string workspaceId,
        string collectionId,
        string relationType,
        CancellationToken cancellationToken = default);
}

/// <summary>存储和查询上下文约束规则。</summary>
public interface IConstraintStore
{
    Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default);

    Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>存储 StableMemory / StableConstraint / DecisionRecord 生命周期人工 review 审核历史。</summary>
public interface IStableLifecycleReviewStore
{
    Task AppendReviewAsync(
        StableLifecycleReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default);
}

/// <summary>存储 CandidateConstraint activate / reject 审核历史。</summary>
public interface ICandidateConstraintReviewStore
{
    Task AppendReviewAsync(
        CandidateConstraintReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default);
}

/// <summary>存储和查询约束语料缺口候选项；不写入正式 ConstraintStore。</summary>
public interface IConstraintGapCandidateStore
{
    Task<ConstraintGapCandidate> SaveAsync(
        ConstraintGapCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<ConstraintGapCandidate?> GetAsync(
        string gapId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default);

    Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task AppendReviewAsync(
        ConstraintGapReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default);
}

/// <summary>存储和查询工作记忆、稳定记忆等分层记忆条目。</summary>
public interface IMemoryStore
{
    Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default);

    Task<ContextMemoryItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        ContextMemoryStatus status,
        CancellationToken cancellationToken = default);
}

/// <summary>存储跨集合或跨工作区复用的全局上下文。</summary>
public interface IGlobalContextStore
{
    Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(
        ContextGlobalQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>记录记忆晋升、拒绝和废弃等生命周期变更。</summary>
public interface IPromotionRecordStore
{
    Task SavePromotionRecordAsync(
        ContextPromotionRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextPromotionRecord>> QueryPromotionRecordsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);
}

/// <summary>存储和查询 Promotion Review 候选项。</summary>
public interface IPromotionCandidateStore
{
    /// <summary>保存或更新候选项。</summary>
    Task SavePromotionCandidateAsync(
        PromotionCandidate candidate,
        CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取候选项。</summary>
    Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>查询候选项，状态为空时返回全部状态。</summary>
    Task<IReadOnlyList<PromotionCandidate>> QueryPromotionCandidatesAsync(
        string workspaceId,
        string collectionId,
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>更新候选项审核状态。</summary>
    Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>执行记忆条目的晋升、拒绝和废弃操作，并产生日志记录。</summary>
public interface IMemoryPromotionService
{
    Task<ContextPromotionRecord> PromoteAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null);

    Task<ContextPromotionRecord> RejectAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null);

    Task<ContextPromotionRecord> DeprecateAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null);
}

/// <summary>评估短期内容是否满足 Promotion 条件；只返回建议，不执行写入。</summary>
public interface IPromotionPolicyEvaluator
{
    /// <summary>根据轻量规则评估候选内容的提升建议。</summary>
    PromotionEvaluationResult Evaluate(PromotionEvaluationRequest request);
}

/// <summary>根据评估结果生成 Promotion Review 候选项。</summary>
public interface IPromotionCandidateFactory
{
    /// <summary>创建候选项；该方法不写入存储。</summary>
    PromotionCandidate CreateCandidate(
        PromotionEvaluationRequest request,
        PromotionEvaluationResult evaluation,
        string sourceKind = "context",
        CancellationToken cancellationToken = default);
}

/// <summary>管理短期工作记忆，供当前上下文打包和运行时决策使用。</summary>
public interface IWorkingMemoryService
{
    Task<WorkingMemoryItem> AddAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);

    Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default);

    Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default);
}
