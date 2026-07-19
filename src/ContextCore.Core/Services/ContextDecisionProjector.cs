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
            .Select(item => EnrichCandidate(new ContextDecisionCandidate
            {
                ItemId = item.ItemId,
                Kind = item.Kind,
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = item.SectionName,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = item.SourceRefs,
                ScoreBreakdown = ConvertScoreBreakdown(item.ScoreBreakdown)
            }));

        var dropped = result.DroppedItems
            .Select(item => EnrichCandidate(new ContextDecisionCandidate
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
            }));

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
            Quality = PackageQualityCalculator.Compute(result),
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
            .Select(item => EnrichCandidate(new ContextDecisionCandidate
            {
                ItemId = string.IsNullOrWhiteSpace(item.CandidateId) ? item.SourceId : item.CandidateId,
                Kind = item.Kind == ContextRetrievalCandidateKind.MemoryItem ? "MemoryItem" : "ContextItem",
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = string.Empty,
                Reason = item.Reasons.Count > 0 ? string.Join("; ", item.Reasons) : string.Empty,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = Array.Empty<string>(),
                ChannelSources = ResolveRetrievalChannelSources(item)
            }));

        var dropped = result.DroppedItems
            .Select(item => EnrichCandidate(new ContextDecisionCandidate
            {
                ItemId = string.IsNullOrWhiteSpace(item.CandidateId) ? item.SourceId : item.CandidateId,
                Kind = item.Kind == ContextRetrievalCandidateKind.MemoryItem ? "MemoryItem" : "ContextItem",
                Type = item.Type,
                Outcome = ContextDecisionCandidateOutcome.Dropped,
                SectionName = string.Empty,
                Reason = item.Reason,
                Score = item.Score,
                EstimatedTokens = item.EstimatedTokens,
                SourceRefs = Array.Empty<string>(),
                ChannelSources = ResolveRetrievalChannelSources(item)
            }));

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

    /// <summary>
    /// R14-1：使用 <see cref="CandidateDecisionReasonCodeMapper"/> 从候选的 Reason 字段
    /// 填充 <see cref="ContextDecisionCandidate.ReasonCode"/> 与 SecondaryReasonCodes。
    /// 幂等：不修改已设置的 ReasonCode；不修改候选的其他字段。
    /// </summary>
    private static ContextDecisionCandidate EnrichCandidate(ContextDecisionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var enriched = CandidateDecisionReasonCodeMapper.EnrichWithReasonCode(candidate);
        var secondary = CandidateDecisionReasonCodeMapper.IdentifySecondaryReasons(enriched);
        if (secondary.Count > 0)
        {
            enriched = enriched with { SecondaryReasonCodes = secondary };
        }
        return enriched;
    }

    /// <summary>
    /// R14-1：将 <see cref="ItemScoreBreakdown"/> 转换为字典形式，
    /// 便于 V2 工具链基于维度名聚合分析评分贡献。
    /// </summary>
    private static IReadOnlyDictionary<string, double> ConvertScoreBreakdown(ItemScoreBreakdown? breakdown)
    {
        if (breakdown is null)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["base"] = breakdown.BaseScore,
            ["layer"] = breakdown.LayerScore,
            ["status"] = breakdown.StatusScore,
            ["semanticAnchor"] = breakdown.SemanticAnchorScore,
            ["rawTokenMatch"] = breakdown.RawTokenMatchScore,
            ["anchorMatchBonus"] = breakdown.AnchorMatchBonus,
            ["modeMatch"] = breakdown.ModeMatchScore,
            ["taskIntent"] = breakdown.TaskIntentScore,
            ["recency"] = breakdown.RecencyScore,
            ["relation"] = breakdown.RelationScore,
            ["lifecyclePenalty"] = breakdown.LifecyclePenalty,
            ["redundancyPenalty"] = breakdown.RedundancyPenalty,
            ["final"] = breakdown.FinalScore
        };
    }

    /// <summary>
    /// R14-1：从检索候选的 Kind 与 Metadata 推断来源 channel。
    /// 同一候选可能由多个 channel 贡献，此处返回单元素列表（向后兼容）。
    /// </summary>
    private static IReadOnlyList<string> ResolveRetrievalChannelSources(ContextRetrievalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var channels = new List<string>();
        if (candidate.Kind == ContextRetrievalCandidateKind.MemoryItem)
        {
            channels.Add("memory");
        }
        else
        {
            channels.Add("context_store");
        }
        if (candidate.Metadata.TryGetValue("channel", out var channel) && !string.IsNullOrEmpty(channel))
        {
            channels.Add(channel);
        }
        return channels;
    }

    /// <summary>
    /// R14-1：从检索决策的 Kind 与 Metadata 推断来源 channel（重载，用于 dropped items）。
    /// </summary>
    private static IReadOnlyList<string> ResolveRetrievalChannelSources(ContextRetrievalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var channels = new List<string>();
        if (decision.Kind == ContextRetrievalCandidateKind.MemoryItem)
        {
            channels.Add("memory");
        }
        else
        {
            channels.Add("context_store");
        }
        if (decision.Metadata.TryGetValue("channel", out var channel) && !string.IsNullOrEmpty(channel))
        {
            channels.Add(channel);
        }
        return channels;
    }
}
