using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>
/// 存储层共享 Normalize/Clone 工具的门面类，重新导出各拆分类型的方法，保持调用方兼容。
/// </summary>
/// <remarks>
/// 历史上这些方法集中在 <c>ContextCore.Abstractions.Models.ContextNormalizers</c>，
/// 迁移到 <c>ContextCore.Storage.Shared</c> 后按模型拆分为多个类型，本门面提供单一入口以最小化调用方改动。
/// </remarks>
public static class CompositeContextNormalizer
{
    public static ContextMemoryItem Normalize(ContextMemoryItem item) => ContextMemoryNormalizer.Normalize(item);
    public static ContextMemoryItem Clone(ContextMemoryItem item) => ContextMemoryNormalizer.Clone(item);

    public static ContextConstraint Normalize(ContextConstraint constraint) => ContextConstraintNormalizer.Normalize(constraint);
    public static ContextConstraint Clone(ContextConstraint item, string? id = null) => ContextConstraintNormalizer.Clone(item, id);

    public static ContextRelation Normalize(ContextRelation relation) => ContextRelationNormalizer.Normalize(relation);
    public static ContextRelation Clone(ContextRelation relation, string? id = null) => ContextRelationNormalizer.Clone(relation, id);

    public static WorkingMemoryItem Normalize(WorkingMemoryItem item) => WorkingMemoryNormalizer.Normalize(item);
    public static WorkingMemoryActiveContext Normalize(WorkingMemoryActiveContext item) => WorkingMemoryNormalizer.Normalize(item);
    public static WorkingMemoryCurrentTask Normalize(WorkingMemoryCurrentTask item) => WorkingMemoryNormalizer.Normalize(item);
    public static PromotionCandidate Normalize(PromotionCandidate item) => WorkingMemoryNormalizer.Normalize(item);

    public static WorkingMemoryActiveContext Clone(WorkingMemoryActiveContext item) => WorkingMemoryNormalizer.Clone(item);
    public static WorkingMemoryCurrentTask Clone(WorkingMemoryCurrentTask item) => WorkingMemoryNormalizer.Clone(item);
    public static PromotionCandidate Clone(
        PromotionCandidate item,
        PromotionCandidateStatus? status = null,
        string? reviewer = null,
        string? reason = null,
        DateTimeOffset? updatedAt = null) => WorkingMemoryNormalizer.Clone(item, status, reviewer, reason, updatedAt);
    public static ContextPromotionRecord Clone(ContextPromotionRecord item) => WorkingMemoryNormalizer.Clone(item);
}
