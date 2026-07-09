using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 把已有的 ContextPackageBuildResult / ContextRetrievalResult 投影为只读 <see cref="ContextDecisionRecord"/>。
/// V17.0：投影过程纯只读，不修改任何输入对象，不触发运行时变更。
/// 所有 <see cref="ContextDecisionRisk"/> 标志位恒为 false（非激活契约）。
/// </summary>
public static class ContextDecisionProjector
{
    /// <summary>从上下文包构建结果投影决策记录。</summary>
    public static ContextDecisionRecord ProjectPackage(ContextPackageBuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selected = result.SelectedItems
            .Select(item => new ContextDecisionCandidate
            {
                ItemId = item.ItemId,
                Kind = item.Kind,
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = item.SectionName,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = item.SourceRefs
            });

        var dropped = result.DroppedItems
            .Select(item => new ContextDecisionCandidate
            {
                ItemId = item.ItemId,
                Kind = item.Kind,
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Dropped,
                SectionName = string.Empty,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = item.SourceRefs
            });

        var sections = result.Package.Sections
            .Where(section => !string.IsNullOrWhiteSpace(section.Name))
            .Select(section => section.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ContextDecisionRecord
        {
            DecisionId = string.IsNullOrWhiteSpace(result.BuildId)
                ? Guid.NewGuid().ToString("N")
                : result.BuildId,
            Source = ContextDecisionSource.Package,
            WorkspaceId = result.Package.WorkspaceId,
            CollectionId = result.Package.CollectionId,
            QueryText = null,
            Candidates = [.. selected, .. dropped],
            Outcome = new ContextDecisionOutcome
            {
                SelectedCount = result.SelectedItems.Count,
                DroppedCount = result.DroppedItems.Count,
                EstimatedTokens = result.EstimatedTokens,
                TokenBudget = result.TokenBudget,
                Sections = sections
            },
            Risk = new ContextDecisionRisk(),
            Metadata = new Dictionary<string, string>(result.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["hasPlan"] = result.Plan is not null ? "true" : "false"
            },
            CreatedAt = result.CreatedAt
        };
    }

    /// <summary>从混合检索结果投影决策记录。只读投影，不改变 result。</summary>
    public static ContextDecisionRecord ProjectRetrieval(ContextRetrievalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selected = result.SelectedItems
            .Select(item => new ContextDecisionCandidate
            {
                ItemId = string.IsNullOrWhiteSpace(item.CandidateId) ? item.SourceId : item.CandidateId,
                Kind = item.Kind == ContextRetrievalCandidateKind.MemoryItem ? "MemoryItem" : "ContextItem",
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = string.Empty,
                Reason = item.Reasons.Count > 0 ? string.Join("; ", item.Reasons) : string.Empty,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = Array.Empty<string>()
            });

        var dropped = result.DroppedItems
            .Select(item => new ContextDecisionCandidate
            {
                ItemId = string.IsNullOrWhiteSpace(item.CandidateId) ? item.SourceId : item.CandidateId,
                Kind = item.Kind == ContextRetrievalCandidateKind.MemoryItem ? "MemoryItem" : "ContextItem",
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Dropped,
                SectionName = string.Empty,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = Array.Empty<string>()
            });

        return new ContextDecisionRecord
        {
            DecisionId = string.IsNullOrWhiteSpace(result.OperationId)
                ? (string.IsNullOrWhiteSpace(result.Trace.RetrievalId)
                    ? Guid.NewGuid().ToString("N")
                    : result.Trace.RetrievalId)
                : result.OperationId,
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = result.Trace.WorkspaceId,
            CollectionId = result.Trace.CollectionId,
            QueryText = result.Trace.QueryText,
            Candidates = [.. selected, .. dropped],
            Outcome = new ContextDecisionOutcome
            {
                SelectedCount = result.SelectedItems.Count,
                DroppedCount = result.DroppedItems.Count,
                EstimatedTokens = result.EstimatedTokens,
                TokenBudget = 0,
                Sections = Array.Empty<string>()
            },
            Risk = new ContextDecisionRisk(),
            Metadata = new Dictionary<string, string>(result.Metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = result.CreatedAt
        };
    }
}
