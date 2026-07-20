using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-4：Package 候选适配器（PackageTraceCandidate → ContextCandidateEnvelope）
//
// 目标：
//   让现有 BasicContextPackageBuilder 路径产出的 PackageTraceCandidate 集合
//   可以转换为统一的 ContextCandidateEnvelope 集合，从而接入 R18-2 的
//   IContextDecisionEngine 编排路径。
//
// 设计原则：
//   1. 适配器是单向转换（PackageTraceCandidate → Envelope），不修改原候选。
//   2. 适配器是幂等的：相同输入产生相同输出。
//   3. 适配器不调用 Engine 或 Storage；纯内存转换。
//   4. 适配器不破坏现有 BasicContextPackageBuilder.BuildDetailedAsync 主链。
//   5. Kind 字符串（如 "working_memory" / "hard_constraint"）映射到
//      ContextCandidateSource 枚举，统一两路径的候选来源表达。
//
// 字段映射：
//   PackageTraceCandidate.Id → envelope.CandidateId
//   PackageTraceCandidate.Kind (string) → envelope.Source (enum)
//   PackageTraceCandidate.Type → envelope.Type
//   PackageTraceCandidate.Score → envelope.Utility.DeterministicScore + FinalScore
//   PackageTraceCandidate.EstimatedTokens → envelope.EstimatedTokens
//   PackageTraceCandidate.SourceRefs → envelope.ProvenanceRefs (封装为 EvidenceRef)
//   PackageTraceCandidate.Metadata → 不直接复制，按字段映射到 Safety/Features
//   PackageTraceCandidate.ScoreBreakdown → envelope.Features.ScoreBreakdown (转字典)
//
// 反向投影（envelope → ContextPackageDecision）由 PackageResultProjector 负责（R18-2）。
// ===========================================================================

/// <summary>
/// R18-4：Package 候选适配器。将 <see cref="PackageTraceCandidate"/>
/// 集合转换为 <see cref="ContextCandidateEnvelope"/> 集合，作为现有 Package
/// 路径与统一 Engine 之间的桥梁。
/// </summary>
public static class PackageCandidateAdapter
{
    /// <summary>
    /// 将单个 <see cref="PackageTraceCandidate"/> 转换为 <see cref="ContextCandidateEnvelope"/>。
    /// </summary>
    public static ContextCandidateEnvelope ToEnvelope(PackageTraceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var source = ResolveCandidateSource(candidate.Kind);
        var isMandatory = IsMandatoryKind(candidate.Kind);
        var lifecycleState = ResolveLifecycleState(candidate.Metadata);

        return new ContextCandidateEnvelope
        {
            CandidateId = candidate.Id,
            Source = source,
            Type = candidate.Type,
            EstimatedTokens = candidate.EstimatedTokens,
            Safety = new CandidateSafetyState
            {
                IsMandatory = isMandatory,
                IsHardConstraint = source == ContextCandidateSource.Mandatory
                                    || source == ContextCandidateSource.Constraint,
                LifecycleState = lifecycleState,
                IsSuperseded = lifecycleState.Equals("superseded", StringComparison.OrdinalIgnoreCase),
                IsDeprecatedUsedByActiveChain = IsDeprecatedUsedByActiveChain(candidate.Metadata),
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = candidate.Score,
                FinalScore = candidate.Score,
                ModelConfidence = 0.0, // Package 路径默认 deterministic-only
                ReasonCode = ResolveReasonCode(candidate.Kind)
            },
            Features = new CandidateFeatureVector
            {
                ScoreBreakdown = ConvertScoreBreakdown(candidate.ScoreBreakdown),
                ChannelSources = ResolveChannelSources(candidate.Kind)
            },
            ProvenanceRefs = candidate.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "package-source-ref",
                    GeneratedAt = DateTimeOffset.UtcNow
                })
                .ToList()
        };
    }

    /// <summary>
    /// 将 <see cref="PackageTraceCandidate"/> 集合批量转换为
    /// <see cref="ContextCandidateEnvelope"/> 集合。
    /// </summary>
    public static IReadOnlyList<ContextCandidateEnvelope> ToEnvelopes(
        IEnumerable<PackageTraceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Select(ToEnvelope).ToList();
    }

    /// <summary>
    /// 将 <see cref="ContextPackageBuildResult"/> 整体转换为
    /// <see cref="ContextDecisionRequest"/>，可直接传入 Engine.DecideAsync。
    /// </summary>
    /// <param name="result">Package 主链产出的结果。</param>
    /// <param name="tokenBudget">token 预算上限。</param>
    /// <param name="enableModel">是否启用模型评分（默认 false）。</param>
    /// <remarks>
    /// 注意：ContextPackageBuildResult.SelectedItems 是 ContextPackageDecision（output DTO），
    /// 不是 PackageTraceCandidate（internal 类型）。此方法只能基于 output DTO 转换，
    /// 因此丢失部分 internal 字段（如 ScoreBreakdown）。如需保留完整字段，
    /// 调用方应在 BuildDetailedAsync 之后直接用 PackageTraceCandidate 集合调用 ToEnvelopes。
    /// </remarks>
    public static ContextDecisionRequest ToDecisionRequest(
        ContextPackageBuildResult result,
        int tokenBudget,
        bool enableModel = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selectedEnvelopes = result.SelectedItems
            .Select(ToEnvelopeFromDecision)
            .ToList();
        var droppedEnvelopes = result.DroppedItems
            .Select(ToEnvelopeFromDroppedItem)
            .ToList();

        var allEnvelopes = new List<ContextCandidateEnvelope>(selectedEnvelopes.Count + droppedEnvelopes.Count);
        allEnvelopes.AddRange(selectedEnvelopes);
        allEnvelopes.AddRange(droppedEnvelopes);

        return new ContextDecisionRequest
        {
            RequestId = result.BuildId,
            DecisionSource = ContextDecisionSource.Package,
            Candidates = allEnvelopes,
            TokenBudget = tokenBudget,
            EnableModel = enableModel
        };
    }

    // ----------------------------------------------------------------------
    // 私有辅助方法
    // ----------------------------------------------------------------------

    private static ContextCandidateSource ResolveCandidateSource(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return ContextCandidateSource.Unknown;

        // 与 PackageTraceRecorder.MapSourceType / ResultProjector 排序逻辑保持一致。
        // ContextCandidateSource 不区分 Raw / CurrentTask，按 channel 语义归并：
        //   - raw/legacy 在 PackageTraceRecorder 中映射到 Keyword channel（lexical 路径）
        //     → ContextCandidateSource.Lexical
        //   - current_task 在枚举注释中明确归入 Recency（"Recency / Task-State"）
        //     → ContextCandidateSource.Recency
        return kind.ToLowerInvariant() switch
        {
            "raw" or "legacy" => ContextCandidateSource.Lexical,
            "current_task" => ContextCandidateSource.Recency,
            "hard_constraint" or "soft_constraint" or "merged_constraint" =>
                ContextCandidateSource.Constraint,
            "working_memory" => ContextCandidateSource.WorkingMemory,
            "stable_memory" => ContextCandidateSource.StableMemory,
            "historical_context" => ContextCandidateSource.StableMemory, // 历史归档视为稳定记忆
            "global_context" => ContextCandidateSource.GlobalContext,
            "recent_context" => ContextCandidateSource.Recency,
            "related_context" => ContextCandidateSource.RelatedContext,
            "constraints" => ContextCandidateSource.Constraint,
            _ => ContextCandidateSource.Unknown
        };
    }

    private static bool IsMandatoryKind(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return false;
        var lower = kind.ToLowerInvariant();
        return lower == "hard_constraint" || lower == "constraints" || lower == "merged_constraint";
    }

    private static string ResolveLifecycleState(Dictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("lifecycleStatus", out var state) && !string.IsNullOrEmpty(state))
        {
            return state;
        }
        return "active";
    }

    private static bool IsDeprecatedUsedByActiveChain(Dictionary<string, string> metadata)
    {
        return metadata.TryGetValue("lifecycleStatus", out var state)
            && state.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
            && metadata.TryGetValue("usedByActiveChain", out var usedStr)
            && bool.TryParse(usedStr, out var used)
            && used;
    }

    private static string ResolveReasonCode(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return "deterministic-only";
        var lower = kind.ToLowerInvariant();
        if (lower == "hard_constraint" || lower == "constraints") return "mandatory";
        return "deterministic-only";
    }

    private static IReadOnlyDictionary<string, double> ConvertScoreBreakdown(ItemScoreBreakdown? breakdown)
    {
        if (breakdown == null) return new Dictionary<string, double>(StringComparer.Ordinal);

        // ItemScoreBreakdown 含 13 个子分维度；此处提取为字典
        var dict = new Dictionary<string, double>(StringComparer.Ordinal);
        var b = breakdown;
        AddIfNonZero(dict, "base", b.BaseScore);
        AddIfNonZero(dict, "layer", b.LayerScore);
        AddIfNonZero(dict, "status", b.StatusScore);
        AddIfNonZero(dict, "semanticAnchor", b.SemanticAnchorScore);
        AddIfNonZero(dict, "rawTokenMatch", b.RawTokenMatchScore);
        AddIfNonZero(dict, "anchorMatchBonus", b.AnchorMatchBonus);
        AddIfNonZero(dict, "modeMatch", b.ModeMatchScore);
        AddIfNonZero(dict, "taskIntent", b.TaskIntentScore);
        AddIfNonZero(dict, "recency", b.RecencyScore);
        AddIfNonZero(dict, "relation", b.RelationScore);
        AddIfNonZero(dict, "lifecyclePenalty", b.LifecyclePenalty);
        AddIfNonZero(dict, "redundancyPenalty", b.RedundancyPenalty);
        AddIfNonZero(dict, "final", b.FinalScore);
        return dict;
    }

    private static void AddIfNonZero(Dictionary<string, double> dict, string key, double value)
    {
        if (value != 0) dict[key] = value;
    }

    private static IReadOnlyList<string> ResolveChannelSources(string kind)
    {
        // Package 路径的 channel 即 Kind 本身
        if (string.IsNullOrEmpty(kind)) return Array.Empty<string>();
        return new[] { kind };
    }

    private static ContextCandidateEnvelope ToEnvelopeFromDecision(ContextPackageDecision decision)
    {
        var source = ResolveCandidateSource(decision.Kind);
        return new ContextCandidateEnvelope
        {
            CandidateId = decision.ItemId,
            Source = source,
            Type = decision.Type,
            EstimatedTokens = decision.EstimatedTokens,
            Safety = new CandidateSafetyState
            {
                IsMandatory = IsMandatoryKind(decision.Kind),
                IsHardConstraint = source == ContextCandidateSource.Mandatory
                                    || source == ContextCandidateSource.Constraint,
                PassesSafetyGate = true
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = decision.Score,
                FinalScore = decision.Score,
                ModelConfidence = 0.0,
                ReasonCode = string.IsNullOrEmpty(decision.Reason) ? "deterministic-only" : decision.Reason
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(decision.Kind)
            },
            ProvenanceRefs = decision.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "package-source-ref",
                    GeneratedAt = DateTimeOffset.UtcNow
                })
                .ToList()
        };
    }

    private static ContextCandidateEnvelope ToEnvelopeFromDroppedItem(DroppedContextItem item)
    {
        var source = ResolveCandidateSource(item.Kind);
        var blockReason = CandidateDecisionReasonCodeMapper.MapFromReason(item.Reason);
        return new ContextCandidateEnvelope
        {
            CandidateId = item.ItemId,
            Source = source,
            Safety = new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = blockReason,
                BlockReasonDetail = item.Reason
            },
            Utility = new CandidateUtilityScore
            {
                ModelConfidence = 0.0,
                ReasonCode = "dropped-by-package"
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(item.Kind)
            }
        };
    }
}
