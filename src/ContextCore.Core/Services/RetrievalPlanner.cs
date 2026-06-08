using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 短期锚定召回计划构建器。
/// 第一版：基于 ShortTermSnapshot 和规则启发式产出 RetrievalPlan。
/// 不调用 LLM，不扩展 embedding，不增加额外存储调用。
/// </summary>
public sealed class RetrievalPlanner
{
    private static readonly ShortTermAnchorExtractor AnchorExtractor = new();
    private readonly ContextAnchorExtractor _contextAnchorExtractor = new();

    /// <summary>
    /// 基于短期快照构建召回计划。
    /// </summary>
    public RetrievalPlan Plan(ShortTermSnapshot snapshot)
    {
        var allEntries = AnchorExtractor.Classify(snapshot.Anchors, snapshot.RecentItems);

        var primary  = allEntries.Where(e => e.Role == RetrievalAnchorRole.Primary).ToArray();
        var support  = allEntries.Where(e => e.Role == RetrievalAnchorRole.Support).ToArray();
        var negative = allEntries.Where(e => e.Role == RetrievalAnchorRole.Negative).ToArray();
        var audit    = allEntries.Where(e => e.Role == RetrievalAnchorRole.Audit).ToArray();
        var conflict = allEntries.Where(e => e.Role == RetrievalAnchorRole.Conflict).ToArray();

        var needsAuditHistory    = audit.Length > 0;
        var needsConflictEvidence = conflict.Length > 0;

        // Stable Memory is needed when there are project/constraint/mode background signals
        var needsStableMemory = support.Any(e =>
            e.AnchorType is AnchorType.Constraint
                         or AnchorType.Project
                         or AnchorType.Mode);

        // ExcludedStatuses: always exclude Rejected; exclude Deprecated only in normal (non-audit/conflict) mode
        var excludedStatuses = new List<string>(2) { "rejected" };
        if (!needsAuditHistory && !needsConflictEvidence)
        {
            excludedStatuses.Add("deprecated");
        }

        return new RetrievalPlan
        {
            PlanId            = Guid.NewGuid().ToString("N"),
            WorkspaceId       = snapshot.WorkspaceId,
            CollectionId      = snapshot.CollectionId,
            QueryText         = snapshot.CurrentQueryText,
            PrimaryAnchors    = primary,
            SupportAnchors    = support,
            NegativeAnchors   = negative,
            AuditAnchors      = audit,
            ConflictAnchors   = conflict,
            NeedsStableMemory     = needsStableMemory,
            NeedsAuditHistory     = needsAuditHistory,
            NeedsConflictEvidence = needsConflictEvidence,
            ExcludedStatuses  = excludedStatuses,
            Snapshot          = snapshot,
            CreatedAt         = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// 从混合检索请求派生短期锚定计划，供未显式传入 Plan 的检索调用使用。
    /// 与 Context Package Builder 共用 ContextAnchorExtractor，避免两条召回链路的锚点规则分叉。
    /// </summary>
    public RetrievalPlan Plan(ContextRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packageRequest = new ContextPackageRequest
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            RequiredTags = request.RequiredTags,
            RequiredTypes = request.RequiredTypes,
            TokenBudget = request.TokenBudget,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
        };

        var anchors = _contextAnchorExtractor.Extract(packageRequest, Array.Empty<RecentContextItem>());
        var snapshot = new ShortTermSnapshot
        {
            WorkspaceId      = request.WorkspaceId,
            CollectionId     = request.CollectionId,
            CurrentQueryText = request.QueryText ?? string.Empty,
            RecentItems      = Array.Empty<RecentContextItem>(),
            Anchors          = anchors,
            CreatedAt        = DateTimeOffset.UtcNow
        };

        return Plan(snapshot);
    }
}
