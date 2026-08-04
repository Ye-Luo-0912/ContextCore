using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 把已有的 ContextPackageBuildResult / ContextRetrievalResult 投影为只读 <see cref="ContextDecisionRecord"/>。
/// 投影过程纯只读，不修改任何输入对象，不触发运行时变更。
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
    /// 使用 <see cref="CandidateDecisionReasonCodeMapper"/> 从候选的 Reason 字段
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
    /// 将 <see cref="ItemScoreBreakdown"/> 转换为字典形式，
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
    /// 从检索候选的 Kind 与 Metadata 推断来源 channel。
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
    /// 从检索决策的 Kind 与 Metadata 推断来源 channel（重载，用于 dropped items）。
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

    // =========================================================================
    // envelope-to-decision 投影路径（新增，不替换 ProjectPackage/ProjectRetrieval）
    // =========================================================================

    /// <summary>
    /// 从 <see cref="ContextDecisionResult"/>（envelope 集合）投影决策记录。
    /// 与 <see cref="ProjectPackage"/> / <see cref="ProjectRetrieval"/> 并存，
    /// 用于 / 阶段的 adapter 路径。envelope 集合保持不变。
    /// </summary>
    /// <param name="result">Engine 输出的决策结果（SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <returns>只读 <see cref="ContextDecisionRecord"/>，所有 Risk 标志位恒为 false。</returns>
    public static ContextDecisionRecord ProjectFromEnvelopes(ContextDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selected = result.SelectedEnvelopes
            .Select(env => EnrichCandidate(new ContextDecisionCandidate
            {
                ItemId = env.CandidateId,
                Kind = ResolveEnvelopeKindString(env.Source),
                Type = env.Type,
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = ResolveEnvelopeSectionName(env.Source),
                Reason = ResolveEnvelopeSelectReason(env),
                Score = env.Utility.FinalScore,
                EstimatedTokens = env.TokenCost?.ContentTokens ?? 0,
                SourceRefs = env.ProvenanceRefs
                    .Where(r => !string.IsNullOrEmpty(r.RefId))
                    .Select(r => r.RefId)
                    .ToList(),
                ChannelSources = env.Features.ChannelSources
            }))
            .ToList();

        var dropped = result.DroppedEnvelopes
            .Select(env => EnrichCandidate(new ContextDecisionCandidate
            {
                ItemId = env.CandidateId,
                Kind = ResolveEnvelopeKindString(env.Source),
                Type = env.Type,
                Outcome = ContextDecisionCandidateOutcome.Dropped,
                SectionName = ResolveEnvelopeSectionName(env.Source),
                Reason = ResolveEnvelopeDropReason(env),
                Score = env.Utility.FinalScore,
                EstimatedTokens = env.TokenCost?.ContentTokens ?? 0,
                SourceRefs = env.ProvenanceRefs
                    .Where(r => !string.IsNullOrEmpty(r.RefId))
                    .Select(r => r.RefId)
                    .ToList(),
                ChannelSources = env.Features.ChannelSources
            }))
            .ToList();

        var candidates = new List<ContextDecisionCandidate>(selected.Count + dropped.Count);
        candidates.AddRange(selected);
        candidates.AddRange(dropped);

        return new ContextDecisionRecord
        {
            DecisionId = result.RequestId,
            Source = result.DecisionSource,
            Candidates = candidates,
            PolicyVersion = result.PolicyVersion,
            Quality = null, // 不计算 quality；由 PackageQualityCalculator 单独计算
            CreatedAt = result.DecidedAt
        };
    }

    private static string ResolveEnvelopeKindString(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "hard_constraint",
        ContextCandidateSource.WorkingMemory => "working_memory",
        ContextCandidateSource.StableMemory => "stable_memory",
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency => "recent_context",
        ContextCandidateSource.Graph or ContextCandidateSource.RelatedContext => "related_context",
        ContextCandidateSource.GlobalContext => "global_context",
        _ => "raw"
    };

    private static string ResolveEnvelopeSectionName(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "hard_constraints",
        ContextCandidateSource.WorkingMemory => "working_memory",
        ContextCandidateSource.StableMemory => "stable_memory",
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency => "recent_context",
        ContextCandidateSource.Graph or ContextCandidateSource.RelatedContext => "related_context",
        ContextCandidateSource.GlobalContext => "global_context",
        _ => "recent_context"
    };

    private static string ResolveEnvelopeSelectReason(ContextCandidateEnvelope env)
    {
        // Source=Mandatory / Constraint 视为 mandatory 类候选；
        // Safety.IsMandatory / IsHardConstraint 同样视为 mandatory 类。
        if (env.Source == ContextCandidateSource.Mandatory || env.Safety.IsMandatory) return "mandatory";
        if (env.Source == ContextCandidateSource.Constraint || env.Safety.IsHardConstraint) return "hard constraint";
        return env.Utility.ModelScore.HasValue ? "model-weighted" : "selected by utility";
    }

    private static string ResolveEnvelopeDropReason(ContextCandidateEnvelope env)
    {
        if (!env.Safety.PassesSafetyGate)
        {
            var code = env.Safety.BlockReasonCode;
            var detail = env.Safety.BlockReasonDetail;
            return string.IsNullOrEmpty(detail) ? code.ToString() : $"{code}: {detail}";
        }
        return "budget exceeded";
    }
}
