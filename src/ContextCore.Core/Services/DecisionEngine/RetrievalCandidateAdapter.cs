using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R18-3：Retrieval 候选适配器（ContextRetrievalCandidate → ContextCandidateEnvelope）
//
// 目标：
//   让现有 HybridContextRetriever 路径产出的 ContextRetrievalCandidate 集合
//   可以转换为统一的 ContextCandidateEnvelope 集合，从而接入 R18-2 的
//   IContextDecisionEngine 编排路径。
//
// 设计原则：
//   1. 适配器是单向转换（ContextRetrievalCandidate → Envelope），不修改原候选。
//   2. 适配器是幂等的：相同输入产生相同输出。
//   3. 适配器不调用 Engine 或 Storage；纯内存转换。
//   4. 适配器保留所有原字段信息（CandidateId/SourceId/Kind/Score/EstimatedTokens/
//      Reasons/SourceRefs/Metadata），并通过 envelope 的 Features/Safety/Utility
//      三个正交维度结构化表达。
//   5. 适配器不破坏现有 HybridContextRetriever.RetrieveAsync 主链；它只是
//      提供一个可选的 envelope 转换入口。
//
// 字段映射：
//   ContextRetrievalCandidate.CandidateId → envelope.CandidateId（首选）
//      备选：SourceId（CandidateId 为空时）
//   ContextRetrievalCandidate.Kind → envelope.Source（枚举映射）
//   ContextRetrievalCandidate.Type → envelope.Type（直传）
//   ContextRetrievalCandidate.Score → envelope.Utility.FinalScore + DeterministicScore
//   ContextRetrievalCandidate.EstimatedTokens → envelope.EstimatedTokens
//   ContextRetrievalCandidate.Reasons → envelope.Utility.ReasonCode（拼接）
//   ContextRetrievalCandidate.SourceRefs → envelope.ProvenanceRefs（封装为 EvidenceRef）
//   ContextRetrievalCandidate.Metadata["mandatory"] → envelope.Safety.IsMandatory
//   ContextRetrievalCandidate.Metadata["lifecycleStatus"] → envelope.Safety.LifecycleState
//
// 反向投影（envelope → ContextRetrievalCandidate）由 RetrievalResultProjector 负责（R18-2）。
// ===========================================================================

/// <summary>
/// R18-3：Retrieval 候选适配器。将 <see cref="ContextRetrievalCandidate"/>
/// 集合转换为 <see cref="ContextCandidateEnvelope"/> 集合，作为现有 Retrieval
/// 路径与统一 Engine 之间的桥梁。
/// </summary>
public static class RetrievalCandidateAdapter
{
    /// <summary>
    /// 将单个 <see cref="ContextRetrievalCandidate"/> 转换为 <see cref="ContextCandidateEnvelope"/>。
    /// </summary>
    public static ContextCandidateEnvelope ToEnvelope(ContextRetrievalCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var candidateId = string.IsNullOrWhiteSpace(candidate.CandidateId)
            ? candidate.SourceId
            : candidate.CandidateId;

        var source = ResolveCandidateSource(candidate);
        var isMandatory = IsMandatoryCandidate(candidate);
        var lifecycleState = ResolveLifecycleState(candidate);

        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
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
                PassesSafetyGate = true // 默认通过；具体拦截由 Engine 阶段决定
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = candidate.Score,
                FinalScore = candidate.Score,
                ModelConfidence = 0.0, // Retrieval 路径默认 deterministic-only
                ReasonCode = ResolveReasonCode(candidate)
            },
            Features = new CandidateFeatureVector
            {
                ChannelSources = ResolveChannelSources(candidate)
            },
            ProvenanceRefs = candidate.SourceRefs
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => new EvidenceRef
                {
                    RefId = r,
                    RefType = "retrieval-source-ref",
                    GeneratedAt = DateTimeOffset.UtcNow
                })
                .ToList()
        };
    }

    /// <summary>
    /// 将 <see cref="ContextRetrievalCandidate"/> 集合批量转换为
    /// <see cref="ContextCandidateEnvelope"/> 集合。
    /// </summary>
    public static IReadOnlyList<ContextCandidateEnvelope> ToEnvelopes(
        IEnumerable<ContextRetrievalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Select(ToEnvelope).ToList();
    }

    /// <summary>
    /// 将 <see cref="ContextRetrievalResult"/> 整体转换为
    /// <see cref="ContextDecisionRequest"/>，可直接传入 Engine.DecideAsync。
    /// </summary>
    /// <param name="result">Retrieval 主链产出的结果。</param>
    /// <param name="tokenBudget">token 预算上限（如 request.TokenBudget）。</param>
    /// <param name="topK">TopK 上限（如 request.TopK）。</param>
    /// <param name="enableModel">是否启用模型评分（默认 false）。</param>
    public static ContextDecisionRequest ToDecisionRequest(
        ContextRetrievalResult result,
        int tokenBudget,
        int topK = int.MaxValue,
        bool enableModel = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        var selectedEnvelopes = ToEnvelopes(result.SelectedItems);
        var droppedEnvelopes = result.DroppedItems
            .Select(dec => ToEnvelopeFromDecision(dec))
            .ToList();

        // 合并 selected + dropped 作为 Engine 输入；Engine 会重新决策
        var allEnvelopes = new List<ContextCandidateEnvelope>(selectedEnvelopes.Count + droppedEnvelopes.Count);
        allEnvelopes.AddRange(selectedEnvelopes);
        allEnvelopes.AddRange(droppedEnvelopes);

        return new ContextDecisionRequest
        {
            RequestId = result.OperationId,
            DecisionSource = ContextDecisionSource.Retrieval,
            Candidates = allEnvelopes,
            TokenBudget = tokenBudget,
            TopK = topK,
            EnableModel = enableModel
        };
    }

    // ----------------------------------------------------------------------
    // 私有辅助方法
    // ----------------------------------------------------------------------

    private static ContextCandidateSource ResolveCandidateSource(ContextRetrievalCandidate candidate)
    {
        // ContextRetrievalCandidate.Metadata 中可能携带 channel / source 信息
        if (candidate.Metadata.TryGetValue("source", out var sourceStr))
        {
            if (Enum.TryParse<ContextCandidateSource>(sourceStr, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        // 默认按 Kind 推断
        return candidate.Kind == ContextRetrievalCandidateKind.MemoryItem
            ? ContextCandidateSource.WorkingMemory
            : ContextCandidateSource.Lexical;
    }

    private static bool IsMandatoryCandidate(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var mandatoryStr)
            && bool.TryParse(mandatoryStr, out var mandatory)
            && mandatory;
    }

    private static string ResolveLifecycleState(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("lifecycleStatus", out var state)
            && !string.IsNullOrEmpty(state)
                ? state
                : "active";
    }

    private static string ResolveReasonCode(ContextRetrievalCandidate candidate)
    {
        if (candidate.Reasons.Count == 0) return "deterministic-only";
        return string.Join(";", candidate.Reasons);
    }

    private static IReadOnlyList<string> ResolveChannelSources(ContextRetrievalCandidate candidate)
    {
        var channels = new List<string>(2);
        if (candidate.Metadata.TryGetValue("channel", out var channel) && !string.IsNullOrEmpty(channel))
        {
            channels.Add(channel);
        }
        if (candidate.Metadata.TryGetValue("retrievalChannel", out var retrievalChannel)
            && !string.IsNullOrEmpty(retrievalChannel))
        {
            channels.Add(retrievalChannel);
        }
        return channels;
    }

    private static ContextCandidateEnvelope ToEnvelopeFromDecision(ContextRetrievalDecision decision)
    {
        var candidateId = string.IsNullOrWhiteSpace(decision.CandidateId)
            ? decision.SourceId
            : decision.CandidateId;
        var source = decision.Kind == ContextRetrievalCandidateKind.MemoryItem
            ? ContextCandidateSource.WorkingMemory
            : ContextCandidateSource.Lexical;

        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = source,
            Type = decision.Type,
            EstimatedTokens = decision.EstimatedTokens,
            Safety = new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCodeMapper.MapFromReason(decision.Reason),
                BlockReasonDetail = decision.Reason
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = decision.Score,
                FinalScore = decision.Score,
                ModelConfidence = 0.0,
                ReasonCode = "dropped-by-retrieval"
            }
        };
    }
}
