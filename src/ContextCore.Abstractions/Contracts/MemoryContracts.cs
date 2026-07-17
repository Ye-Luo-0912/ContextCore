using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>图遍历方向。</summary>
public enum RelationDirection
{
    /// <summary>仅出边（source → target）。</summary>
    Outgoing,
    /// <summary>仅入边（target ← source）。</summary>
    Incoming,
    /// <summary>出边和入边（默认）。</summary>
    Both
}

/// <summary>存储和查询上下文条目之间的有向关系。</summary>
/// <remarks>
/// GRAPH-11：接口精简为 5 个核心方法 + SaveAsync 薄包装。
/// 核心：Get/Delete/BatchUpsert/Query/QueryNeighbors(RelationNeighborQuery)。
/// SaveAsync 保留为单条便利方法，实现委托 BatchUpsertAsync；旧 SaveMany/QueryForItem/QueryBySource/QueryByTarget/QueryByType 已移除，统一走 Query。
/// </remarks>
public interface IRelationStore
{
    /// <summary>保存或更新一条关系。等价于 BatchUpsertAsync([relation])，保留为单条便利方法。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default);

    /// <summary>按条件查询关系。SourceId/TargetId/ItemId/RelationType 均通过 ContextRelationQuery 过滤，取代旧 QueryBy* 方法。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取单条关系。不存在时返回 null。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default);

    /// <summary>按 ID 删除单条关系。返回是否删除成功。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default);

    /// <summary>批量 upsert，实现应在单连接单事务中完成（Postgres）或原子写入（FileSystem）。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GRAPH-10：统一邻居查询。携带方向、类型、置信度、生命周期、分页和扫描上限。
    /// Postgres 在 SQL 中过滤和 Limit；File/InMemory 在内存中过滤。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P3-04：关系回填策略接口。生产 Core 使用默认实现（不回填），
/// Evaluation 工具使用 eval/fixture/deterministic 感知实现。
/// 将 eval 特判从生产 Core 移到 Evaluation 工具层。
/// </summary>
public interface IRelationBackfillPolicy
{
    /// <summary>判断关系是否可确定性回填证据（eval fixture / deterministic 关系）。</summary>
    bool CanBackfillDeterministicEvidence(ContextRelation relation);

    /// <summary>标准化关系类型并回填 fixture 元数据（eval corpus hygiene 专用）。</summary>
    ContextRelation NormalizeAndBackfillFixtureRelation(
        ContextRelation relation,
        string sourceOperationId = "relation-corpus-hygiene-g5.1");
}

/// <summary>
/// 统一的关系生产投影器，在 Ingest/Compression/Promotion/Lifecycle Review 四个流程中生成图边。
/// 实现负责统一填充 GRAPH-01 契约字段（SourceNodeKind/TargetNodeKind/Lifecycle/ReviewStatus/UpdatedAt/Provenance）。
/// 生成的关系列表由调用者通过 IRelationStore.BatchUpsertAsync 落库。
/// </summary>
public interface IRelationProjector
{
    /// <summary>Ingest 流程：从 <see cref="ContextItem.Refs"/> 生成 related_to 关系。</summary>
    IReadOnlyList<ContextRelation> ProjectForIngest(ContextItem item);

    /// <summary>Compression 流程：从压缩响应生成 derived_from/summarizes/generated_by 关系。</summary>
    IReadOnlyList<ContextRelation> ProjectForCompression(CompressionResponse response);

    /// <summary>Promotion 流程：从晋升候选生成 promoted_from/derived_from/evidence_for 关系。</summary>
    /// <param name="candidate">晋升候选项。</param>
    /// <param name="targetItemId">晋升目标条目 ID（mem:stp: 或 constraint:stp: 前缀）。</param>
    /// <param name="targetKind">目标种类（"memory" 或 "constraint"）。</param>
    /// <param name="now">投影时间戳。</param>
    IReadOnlyList<ContextRelation> ProjectForPromotion(
        ShortTermPromotionCandidate candidate,
        string targetItemId,
        string targetKind,
        DateTimeOffset now);

    /// <summary>Lifecycle Review 流程：从 supersede 生成 superseded_by/replaces 关系对。</summary>
    IReadOnlyList<ContextRelation> ProjectForSupersede(SupersedeProjectionRequest request);
}

/// <summary>统一的关系投影写入边界：验证 + 落库。</summary>
public interface IRelationProjectionWriter
{
    Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        CancellationToken cancellationToken = default);
}

/// <summary>存储 Relation review / lifecycle 人工操作审核历史。</summary>
public interface IRelationReviewStore
{
    Task AppendReviewAsync(
        RelationReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default);
}

/// <summary>存储和查询上下文约束规则。</summary>
public interface IConstraintStore
{
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
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
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<ContextMemoryItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Write)]
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
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
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
    [StoreOperation(StoreOperationKind.Write)]
    Task<WorkingMemoryItem> AddAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Write)]
    Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Write)]
    Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Read)]
    Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);

    [StoreOperation(StoreOperationKind.Write)]
    Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default);
}
